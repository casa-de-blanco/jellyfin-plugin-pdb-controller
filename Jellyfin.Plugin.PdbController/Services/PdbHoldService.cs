using System.Globalization;
using System.Threading.Channels;
using Jellyfin.Plugin.PdbController.Configuration;
using Jellyfin.Plugin.PdbController.Kubernetes;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PdbController.Services;

/// <summary>
/// Keeps a PodDisruptionBudget in step with what Jellyfin is doing.
/// </summary>
/// <remarks>
/// Enumeration is the source of truth, not events. Every tick asks the task manager
/// and the session manager what is happening right now; the events only wake the loop
/// up early. That way a missed, swallowed or out-of-order event costs one reconcile
/// interval instead of stranding the budget in the wrong state.
/// </remarks>
public sealed class PdbHoldService : IHostedService, IDisposable
{
    private readonly ILogger<PdbHoldService> _logger;
    private readonly IKubernetesPdbClient _client;
    private readonly ITaskManager _taskManager;
    private readonly ISessionManager _sessionManager;

    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly CancellationTokenSource _stopping = new();

    private Task? _loop;
    private DateTimeOffset? _heldSince;
    private DateTimeOffset? _clearedAt;
    private bool _forceReleased;
    private string? _lastFailure;
    private DateTimeOffset _lastFailureLoggedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="PdbHoldService"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="client">Kubernetes client.</param>
    /// <param name="taskManager">Scheduled task manager.</param>
    /// <param name="sessionManager">Session manager.</param>
    public PdbHoldService(
        ILogger<PdbHoldService> logger,
        IKubernetesPdbClient client,
        ITaskManager taskManager,
        ISessionManager sessionManager)
    {
        _logger = logger;
        _client = client;
        _taskManager = taskManager;
        _sessionManager = sessionManager;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe before the loop starts, so nothing that happens during the first
        // reconcile is missed.
        _taskManager.TaskExecuting += OnChanged;
        _taskManager.TaskCompleted += OnChanged;
        _sessionManager.PlaybackStart += OnChanged;
        _sessionManager.PlaybackStopped += OnChanged;
        _sessionManager.SessionStarted += OnChanged;
        _sessionManager.SessionEnded += OnChanged;

        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _taskManager.TaskExecuting -= OnChanged;
        _taskManager.TaskCompleted -= OnChanged;
        _sessionManager.PlaybackStart -= OnChanged;
        _sessionManager.PlaybackStopped -= OnChanged;
        _sessionManager.SessionStarted -= OnChanged;
        _sessionManager.SessionEnded -= OnChanged;

        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        // Best effort release on the way out, on our own deadline rather than the
        // caller's -- the token passed to StopAsync is already cancelling. This is
        // what stops a scale-to-zero during a hold from leaving a budget held open
        // with no pod left to release it.
        await TryReleaseOnShutdownAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Dispose();
    }

    private static string Describe(IReadOnlyList<string> tasks, IReadOnlyList<string> users)
    {
        var parts = new List<string>();
        if (tasks.Count > 0)
        {
            parts.Add("tasks: " + string.Join(", ", tasks));
        }

        if (users.Count > 0)
        {
            parts.Add("streaming: " + string.Join(", ", users));
        }

        return parts.Count > 0 ? string.Join("; ", parts) : "nothing";
    }

    private void OnChanged(object? sender, EventArgs e) => _wake.Writer.TryWrite(true);

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e) => _wake.Writer.TryWrite(true);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // The very first pass is the crash-recovery pass. A SIGKILL mid-hold leaves
        // minAvailable at 1 with no process left to put it back, and that blocks
        // every drain until someone notices -- so reconciling before anything else
        // is the single most important thing this service does.
        while (!cancellationToken.IsCancellationRequested)
        {
            await ReconcileSafelyAsync(cancellationToken).ConfigureAwait(false);

            var interval = TimeSpan.FromSeconds(Math.Clamp(Configuration?.ReconcileIntervalSeconds ?? 60, 5, 3600));

            using var delay = new CancellationTokenSource(interval);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, delay.Token);

            try
            {
                // Whichever comes first: an event, or the ticker.
                await _wake.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false);
                _wake.Reader.TryRead(out _);
            }
            catch (OperationCanceledException)
            {
                // Either the interval elapsed or we are shutting down; the loop
                // condition tells the two apart.
            }
        }
    }

    private PluginConfiguration? Configuration => Plugin.Instance?.Configuration;

    private async Task ReconcileSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReconcileAsync(cancellationToken).ConfigureAwait(false);
            _lastFailure = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Nothing in here may take Jellyfin down with it. A budget that cannot
            // be written is worth a log line, never a faulted host.
            LogFailure(ex);
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var config = Configuration;
        if (config is null || !config.Enabled || !_client.Available)
        {
            return;
        }

        var namespaceName = KubernetesEnvironment.ResolveNamespace(config.Namespace);
        if (string.IsNullOrEmpty(namespaceName) || string.IsNullOrWhiteSpace(config.PdbName))
        {
            return;
        }

        var holdingTasks = RunningSelectedTasks(config);
        var streamingUsers = StreamingSelectedUsers(config);
        var wantHold = holdingTasks.Count > 0 || streamingUsers.Count > 0;
        var now = DateTimeOffset.UtcNow;

        if (!wantHold)
        {
            // Once the reason is genuinely gone the latch has served its purpose.
            if (_forceReleased)
            {
                _logger.LogInformation("Hold conditions cleared; force-release latch lifted.");
                _forceReleased = false;
            }

            _clearedAt ??= now;
        }
        else
        {
            _clearedAt = null;
        }

        var state = await _client.GetAsync(namespaceName, config.PdbName, cancellationToken).ConfigureAwait(false);

        if (state.UsesMaxUnavailable)
        {
            throw new PdbApiException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{namespaceName}/{config.PdbName} is expressed with maxUnavailable; refusing to patch minAvailable onto it."));
        }

        var desired = DecideDesired(config, state, wantHold, holdingTasks, streamingUsers, now);

        if (state.MinAvailable == desired)
        {
            _logger.LogDebug(
                "{Namespace}/{Name} already at minAvailable {Value}; holding: {Reason}",
                namespaceName,
                config.PdbName,
                desired,
                Describe(holdingTasks, streamingUsers));

            if (desired == 1)
            {
                _heldSince ??= now;
            }
            else
            {
                _heldSince = null;
            }

            return;
        }

        var reason = Describe(holdingTasks, streamingUsers);
        DateTimeOffset? heldSince = desired == 1 ? _heldSince ?? now : null;

        await _client.PatchMinAvailableAsync(
            namespaceName,
            config.PdbName,
            desired,
            heldSince,
            reason,
            cancellationToken).ConfigureAwait(false);

        _heldSince = heldSince;

        _logger.LogInformation(
            "{Namespace}/{Name} minAvailable {Old} -> {New} ({Reason})",
            namespaceName,
            config.PdbName,
            state.MinAvailable?.ToString(CultureInfo.InvariantCulture) ?? "unset",
            desired,
            reason);
    }

    private int DecideDesired(
        PluginConfiguration config,
        PdbState state,
        bool wantHold,
        IReadOnlyList<string> holdingTasks,
        IReadOnlyList<string> streamingUsers,
        DateTimeOffset now)
    {
        if (!wantHold)
        {
            // Release is delayed; taking the hold is not. A paused film or a client
            // reconnecting should not flap the budget.
            var grace = TimeSpan.FromSeconds(Math.Max(0, config.GracePeriodSeconds));
            if (_clearedAt is { } cleared && now - cleared < grace)
            {
                _logger.LogDebug("Within the grace period; keeping minAvailable at {Value}.", state.MinAvailable ?? 0);
                return state.MinAvailable ?? 0;
            }

            return 0;
        }

        if (_forceReleased)
        {
            // The latch is the whole point of a maximum hold. Without it the next
            // tick sees the same running task, re-holds, and the cap means nothing.
            return 0;
        }

        var maxHold = TimeSpan.FromMinutes(Math.Max(1, config.MaxHoldMinutes));
        if (state.MinAvailable == 1 && _heldSince is { } since && now - since > maxHold)
        {
            _logger.LogWarning(
                "Breaking a hold that has lasted {Elapsed} (limit {Limit}); still holding: {Reason}. "
                + "The budget is being released so node drains and upgrades can proceed.",
                now - since,
                maxHold,
                Describe(holdingTasks, streamingUsers));

            _forceReleased = true;
            return 0;
        }

        return 1;
    }

    private List<string> RunningSelectedTasks(PluginConfiguration config)
    {
        var running = new List<string>();

        foreach (var worker in _taskManager.ScheduledTasks)
        {
            if (worker.State != TaskState.Running)
            {
                continue;
            }

            // Key, not Name: names are localised, and Id is regenerated per install.
            var key = worker.ScheduledTask.Key;
            if (config.AllTasks || config.TaskKeys.Contains(key, StringComparer.Ordinal))
            {
                running.Add(worker.Name);
            }
        }

        return running;
    }

    private List<string> StreamingSelectedUsers(PluginConfiguration config)
    {
        var streaming = new List<string>();

        foreach (var session in _sessionManager.Sessions)
        {
            // NowPlayingItem is what separates "watching" from "connected". An idle
            // client sitting on a home screen has none, and must not hold the budget
            // open forever. A *paused* session does have one and deliberately still
            // counts -- a drain should not kill a paused film either.
            if (session.NowPlayingItem is null)
            {
                continue;
            }

            if (config.AnyUser || IsSelected(config, session))
            {
                streaming.Add(session.UserName ?? session.UserId.ToString("N", CultureInfo.InvariantCulture));
            }
        }

        return streaming;
    }

    private static bool IsSelected(PluginConfiguration config, SessionInfo session)
    {
        if (config.UserIds.Contains(session.UserId.ToString("N", CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // A shared session can carry additional users; any selected one of them
        // holds the budget.
        foreach (var additional in session.AdditionalUsers)
        {
            if (config.UserIds.Contains(additional.UserId.ToString("N", CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task TryReleaseOnShutdownAsync()
    {
        var config = Configuration;
        if (config is null || !config.Enabled || !_client.Available || _heldSince is null)
        {
            return;
        }

        var namespaceName = KubernetesEnvironment.ResolveNamespace(config.Namespace);
        if (string.IsNullOrEmpty(namespaceName))
        {
            return;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await _client.PatchMinAvailableAsync(
                namespaceName,
                config.PdbName,
                0,
                null,
                "shutting down",
                deadline.Token).ConfigureAwait(false);

            _logger.LogInformation("Released {Namespace}/{Name} on shutdown.", namespaceName, config.PdbName);
        }
        catch (Exception ex) when (ex is PdbApiException or HttpRequestException or OperationCanceledException or IOException)
        {
            // Best effort by definition. If this does not land, the next start
            // reconciles it -- that is what the startup pass is for.
            _logger.LogWarning(ex, "Could not release {Namespace}/{Name} on shutdown.", namespaceName, config.PdbName);
        }
    }

    private void LogFailure(Exception ex)
    {
        var signature = ex is PdbApiException api
            ? string.Create(CultureInfo.InvariantCulture, $"api:{api.StatusCode}")
            : ex.GetType().FullName ?? "unknown";

        var now = DateTimeOffset.UtcNow;

        // A standing 403 should say so once, not sixty times an hour -- but it must
        // not go silent forever either, or a long-running fault leaves no trace.
        if (string.Equals(_lastFailure, signature, StringComparison.Ordinal)
            && now - _lastFailureLoggedAt < TimeSpan.FromMinutes(15))
        {
            _logger.LogDebug(ex, "Reconcile failed again ({Signature}).", signature);
            return;
        }

        _lastFailure = signature;
        _lastFailureLoggedAt = now;
        _logger.LogError(ex, "Reconcile failed ({Signature}).", signature);
    }
}
