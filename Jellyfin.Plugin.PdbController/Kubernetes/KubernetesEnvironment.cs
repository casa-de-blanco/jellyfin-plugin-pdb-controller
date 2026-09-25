using System.Globalization;

namespace Jellyfin.Plugin.PdbController.Kubernetes;

/// <summary>
/// The in-cluster credentials and endpoint, read from the projected service
/// account directory and the environment the kubelet injects.
/// </summary>
public static class KubernetesEnvironment
{
    /// <summary>Directory the kubelet projects the service account into.</summary>
    public const string ServiceAccountDirectory = "/var/run/secrets/kubernetes.io/serviceaccount";

    /// <summary>Path of the bearer token.</summary>
    public static string TokenPath => Path.Combine(ServiceAccountDirectory, "token");

    /// <summary>Path of the cluster CA bundle.</summary>
    public static string CaCertificatePath => Path.Combine(ServiceAccountDirectory, "ca.crt");

    /// <summary>Path of the file naming the pod's own namespace.</summary>
    public static string NamespacePath => Path.Combine(ServiceAccountDirectory, "namespace");

    /// <summary>
    /// Gets a value indicating whether this process looks like it is running inside
    /// a cluster with a mounted service account.
    /// </summary>
    public static bool InCluster => File.Exists(TokenPath) && File.Exists(CaCertificatePath);

    /// <summary>
    /// Gets the API server base address. Taken from the environment rather than the
    /// well-known DNS name, matching the official clients -- it keeps working when
    /// cluster DNS does not.
    /// </summary>
    /// <returns>The base URI of the API server.</returns>
    public static Uri GetApiServerUri()
    {
        var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT_HTTPS")
                   ?? Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT");

        if (string.IsNullOrWhiteSpace(host))
        {
            return new Uri("https://kubernetes.default.svc:443");
        }

        // An IPv6 literal has to be bracketed before it can go in a URI.
        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('['))
        {
            host = "[" + host + "]";
        }

        var portNumber = int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 443;

        return new Uri(string.Create(CultureInfo.InvariantCulture, $"https://{host}:{portNumber}"));
    }

    /// <summary>
    /// Reads the bearer token. Deliberately re-read on every call: projected tokens
    /// are rotated at around 80% of their lifetime, and this plugin can idle for
    /// hours between writes -- so a token cached at startup is guaranteed to 401
    /// exactly when it is finally needed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current bearer token.</returns>
    public static async Task<string> ReadTokenAsync(CancellationToken cancellationToken)
    {
        var token = await File.ReadAllTextAsync(TokenPath, cancellationToken).ConfigureAwait(false);
        return token.Trim();
    }

    /// <summary>
    /// Resolves the namespace to operate in: the configured value, else the pod's
    /// own namespace.
    /// </summary>
    /// <param name="configured">The configured namespace, possibly empty.</param>
    /// <returns>The namespace, or an empty string if it could not be determined.</returns>
    public static string ResolveNamespace(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        try
        {
            return File.Exists(NamespacePath)
                ? File.ReadAllText(NamespacePath).Trim()
                : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
