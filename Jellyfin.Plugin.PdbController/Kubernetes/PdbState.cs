namespace Jellyfin.Plugin.PdbController.Kubernetes;

/// <summary>
/// The parts of a PodDisruptionBudget this plugin cares about.
/// </summary>
/// <param name="MinAvailable">The observed <c>spec.minAvailable</c>.</param>
/// <param name="UsesMaxUnavailable">
/// Whether the budget is expressed with <c>maxUnavailable</c> instead. The two are
/// mutually exclusive in the API, so a merge patch adding minAvailable to such a
/// budget is rejected -- we refuse to write rather than fail every tick.
/// </param>
public readonly record struct PdbState(int? MinAvailable, bool UsesMaxUnavailable);
