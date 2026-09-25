namespace Jellyfin.Plugin.PdbController.Kubernetes;

/// <summary>
/// Reads and toggles a single PodDisruptionBudget.
/// </summary>
public interface IKubernetesPdbClient
{
    /// <summary>
    /// Gets a value indicating whether the client found usable in-cluster credentials.
    /// </summary>
    bool Available { get; }

    /// <summary>Reads the current state of the budget.</summary>
    /// <param name="namespaceName">Namespace holding the budget.</param>
    /// <param name="name">Name of the budget.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The observed state.</returns>
    Task<PdbState> GetAsync(string namespaceName, string name, CancellationToken cancellationToken);

    /// <summary>Sets <c>spec.minAvailable</c>, and stamps why.</summary>
    /// <param name="namespaceName">Namespace holding the budget.</param>
    /// <param name="name">Name of the budget.</param>
    /// <param name="minAvailable">The value to write.</param>
    /// <param name="heldSince">When the current hold began, or null when releasing.</param>
    /// <param name="reason">Short human-readable reason, recorded as an annotation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    Task PatchMinAvailableAsync(
        string namespaceName,
        string name,
        int minAvailable,
        DateTimeOffset? heldSince,
        string reason,
        CancellationToken cancellationToken);
}
