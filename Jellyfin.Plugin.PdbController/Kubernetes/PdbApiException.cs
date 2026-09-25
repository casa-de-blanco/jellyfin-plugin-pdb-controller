using System.Net;

namespace Jellyfin.Plugin.PdbController.Kubernetes;

/// <summary>
/// A non-success response from the Kubernetes API server.
/// </summary>
public class PdbApiException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PdbApiException"/> class.</summary>
    public PdbApiException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PdbApiException"/> class.</summary>
    /// <param name="message">Message.</param>
    public PdbApiException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PdbApiException"/> class.</summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Inner exception.</param>
    public PdbApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PdbApiException"/> class.</summary>
    /// <param name="message">Message.</param>
    /// <param name="statusCode">The status code returned by the API server.</param>
    public PdbApiException(string message, HttpStatusCode statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>Gets the status code returned by the API server, when there was one.</summary>
    public HttpStatusCode? StatusCode { get; }
}
