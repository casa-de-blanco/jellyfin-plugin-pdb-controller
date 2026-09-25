using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PdbController.Configuration;

/// <summary>
/// Plugin settings. Jellyfin persists this with <see cref="System.Xml.Serialization.XmlSerializer"/>,
/// so every member has to be a public settable property of a serializable type --
/// no dictionaries, no interfaces, and a public parameterless constructor.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether the controller runs at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the namespace holding the budget. Empty means "read it from the
    /// service account directory", which is right in every normal deployment.
    /// </summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the PodDisruptionBudget to toggle.</summary>
    public string PdbName { get; set; } = "jellyfin";

    /// <summary>
    /// Gets or sets a value indicating whether every running task holds the budget.
    /// When true <see cref="TaskKeys"/> is ignored but deliberately preserved, so
    /// clearing this restores the previous selection -- and a task added by a later
    /// plugin or server upgrade is covered without anyone revisiting the page.
    /// </summary>
    public bool AllTasks { get; set; }

    /// <summary>
    /// Gets or sets the scheduled tasks that hold the budget, by
    /// <c>IScheduledTask.Key</c>. Not Name (localised) and not Id (regenerated per
    /// install), so a rename or a reinstall does not silently drop the selection.
    /// </summary>
    public List<string> TaskKeys { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether any user's playback holds the budget.</summary>
    public bool AnyUser { get; set; }

    /// <summary>Gets or sets the users whose playback holds the budget, by user id.</summary>
    public List<string> UserIds { get; set; } = new();

    /// <summary>
    /// Gets or sets how long to wait after the last condition clears before releasing.
    /// A paused film or a client reconnecting mid-stream should not cause a
    /// release-then-hold flap.
    /// </summary>
    public int GracePeriodSeconds { get; set; } = 300;

    /// <summary>
    /// Gets or sets the longest a single hold may last before it is broken.
    /// A wedged task or a session Jellyfin never reaped would otherwise block node
    /// drains and Talos upgrades indefinitely.
    /// </summary>
    public int MaxHoldMinutes { get; set; } = 240;

    /// <summary>
    /// Gets or sets the reconcile interval. Events only make the loop responsive;
    /// this ticker is what makes it correct, so it is not optional.
    /// </summary>
    public int ReconcileIntervalSeconds { get; set; } = 60;
}
