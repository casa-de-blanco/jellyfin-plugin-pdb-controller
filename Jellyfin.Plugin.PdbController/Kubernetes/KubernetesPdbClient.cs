using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PdbController.Kubernetes;

/// <summary>
/// A deliberately tiny Kubernetes client: one GET and one PATCH against one object.
/// </summary>
/// <remarks>
/// This does not use the KubernetesClient NuGet package, and that is not a style
/// preference. Every plugin is loaded into a collectible AssemblyLoadContext whose
/// dependencies are resolved from the plugin directory alone, and the plugin manager
/// then calls GetTypes() on the assembly. Any transitive dependency that is missing
/// or that disagrees with the server's own copy turns the plugin into NotSupported
/// with a message about incompatible shared libraries -- a failure that shows up only
/// on the target server and never in a local build. Two HTTP calls are not worth it,
/// so the plugin ships as a single DLL with no runtime dependencies.
/// </remarks>
public sealed class KubernetesPdbClient : IKubernetesPdbClient, IDisposable
{
    /// <summary>Annotation recording when the current hold started.</summary>
    public const string HoldSinceAnnotation = "casa-de-blan.co/hold-since";

    /// <summary>Annotation recording what is holding the budget.</summary>
    public const string HoldReasonAnnotation = "casa-de-blan.co/hold-reason";

    private readonly ILogger<KubernetesPdbClient> _logger;
    private readonly HttpClient? _httpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="KubernetesPdbClient"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public KubernetesPdbClient(ILogger<KubernetesPdbClient> logger)
    {
        _logger = logger;

        if (!KubernetesEnvironment.InCluster)
        {
            // Not an error worth repeating: outside a cluster, or with the pod's
            // automountServiceAccountToken left at app-template's default of false,
            // there is simply nothing to talk to. Say so once and stay quiet.
            _logger.LogWarning(
                "No Kubernetes service account found at {Path}; the PDB controller will do nothing. "
                + "In-cluster this usually means automountServiceAccountToken is false on the pod.",
                KubernetesEnvironment.ServiceAccountDirectory);
            return;
        }

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = ValidateServerCertificate,
            },
        };

        _httpClient = new HttpClient(handler) { BaseAddress = KubernetesEnvironment.GetApiServerUri() };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        Available = true;
    }

    /// <inheritdoc />
    public bool Available { get; }

    /// <inheritdoc />
    public async Task<PdbState> GetAsync(string namespaceName, string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildPath(namespaceName, name));
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, namespaceName, name);

        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var heldSince = ReadHeldSince(document.RootElement);

        if (!document.RootElement.TryGetProperty("spec", out var spec))
        {
            return new PdbState(null, false, heldSince);
        }

        var usesMaxUnavailable = spec.TryGetProperty("maxUnavailable", out _);

        if (!spec.TryGetProperty("minAvailable", out var minAvailable))
        {
            return new PdbState(null, usesMaxUnavailable, heldSince);
        }

        // minAvailable is an IntOrString, so it is legitimately either. A percentage
        // ("50%") parses as neither and is left as null, which the caller treats as
        // "not a budget I understand" rather than guessing.
        return minAvailable.ValueKind switch
        {
            JsonValueKind.Number => new PdbState(minAvailable.GetInt32(), usesMaxUnavailable, heldSince),
            JsonValueKind.String when int.TryParse(
                minAvailable.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed) => new PdbState(parsed, usesMaxUnavailable, heldSince),
            _ => new PdbState(null, usesMaxUnavailable, heldSince),
        };
    }

    /// <inheritdoc />
    public async Task PatchMinAvailableAsync(
        string namespaceName,
        string name,
        int minAvailable,
        DateTimeOffset? heldSince,
        string reason,
        CancellationToken cancellationToken)
    {
        var since = heldSince?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        var patch = JsonSerializer.Serialize(new
        {
            metadata = new
            {
                annotations = new Dictionary<string, string?>
                {
                    // A null value removes the annotation under a merge patch, which
                    // is what clears the stamp on release.
                    [HoldSinceAnnotation] = since,
                    [HoldReasonAnnotation] = heldSince is null ? null : reason,
                },
            },
            spec = new { minAvailable },
        });

        using var request = new HttpRequestMessage(HttpMethod.Patch, BuildPath(namespaceName, name))
        {
            // Merge patch, not strategic-merge: minAvailable is a scalar and this
            // leaves the selector and everything else Argo owns untouched.
            Content = new StringContent(patch, Encoding.UTF8, "application/merge-patch+json"),
        };

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, namespaceName, name);
    }

    /// <inheritdoc />
    public void Dispose() => _httpClient?.Dispose();

    /// <summary>
    /// Reads the hold-since stamp off the object, if this plugin left one there.
    /// </summary>
    private static DateTimeOffset? ReadHeldSince(JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out var metadata)
            || !metadata.TryGetProperty("annotations", out var annotations)
            || annotations.ValueKind != JsonValueKind.Object
            || !annotations.TryGetProperty(HoldSinceAnnotation, out var since)
            || since.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            since.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }

    private static string BuildPath(string namespaceName, string name) =>
        FormattableString.Invariant(
            $"/apis/policy/v1/namespaces/{Uri.EscapeDataString(namespaceName)}/poddisruptionbudgets/{Uri.EscapeDataString(name)}");

    /// <summary>
    /// Validates the API server certificate against the cluster CA only.
    /// </summary>
    /// <remarks>
    /// Not a blanket "return true". The CA is pinned as a custom root, but the name
    /// check is kept: the base address is an IP taken from the environment, so a
    /// mismatch there is a real signal rather than noise to be suppressed.
    /// </remarks>
    private static bool ValidateServerCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
            || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)
            || certificate is null)
        {
            return false;
        }

        try
        {
            using var caCertificate = X509CertificateLoader.LoadCertificateFromFile(
                KubernetesEnvironment.CaCertificatePath);
            using var serverCertificate = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            using var customChain = new X509Chain();

            customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            customChain.ChainPolicy.CustomTrustStore.Add(caCertificate);
            customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            customChain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

            return customChain.Build(serverCertificate);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException)
        {
            _ = ex;
            return false;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var client = _httpClient
                     ?? throw new InvalidOperationException("No in-cluster Kubernetes credentials.");

        var token = await KubernetesEnvironment.ReadTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureSuccess(HttpResponseMessage response, string namespaceName, string name)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // Each of these means something different to the operator, so name it rather
        // than letting one "request failed" cover all of them.
        var hint = response.StatusCode switch
        {
            HttpStatusCode.NotFound =>
                "the budget does not exist -- it is GitOps-owned and this plugin will never create it",
            HttpStatusCode.Unauthorized =>
                "the service account token was rejected; it will be re-read on the next attempt",
            HttpStatusCode.Forbidden =>
                "RBAC denied the call; the pod's service account needs get and patch on this budget",
            _ => "transient or unexpected API server error",
        };

        throw new PdbApiException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)response.StatusCode} on {namespaceName}/{name}: {hint}"),
            response.StatusCode);
    }
}
