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
/// <param name="HeldSince">
/// When the hold recorded on the object began, from the hold-since annotation. This
/// is the only durable record of it: a restart during a genuine hold would otherwise
/// start the maximum-hold clock again from zero, and a long scan could then outlast
/// the cap indefinitely by being restarted through it.
/// </param>
public readonly record struct PdbState(int? MinAvailable, bool UsesMaxUnavailable, DateTimeOffset? HeldSince);
