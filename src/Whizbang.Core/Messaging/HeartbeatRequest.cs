using System.Text.Json;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Parameters for <see cref="IWorkCoordinator.RecordHeartbeatAsync"/>.
/// Decoupled from the polling-loop call so the heartbeat timer can fire on its own
/// cadence independent of work claim cadence.
/// </summary>
/// <param name="InstanceId">This service instance's identity.</param>
/// <param name="ServiceName">Service name (used for diagnostics and cross-instance grouping).</param>
/// <param name="HostName">Pod / host name.</param>
/// <param name="ProcessId">OS process id.</param>
/// <param name="Metadata">Optional metadata blob; defaults to empty object.</param>
/// <param name="LifecyclePhase">
/// The instance's current lifecycle phase as text, or null to leave the registry's value alone.
/// The heartbeat carries it so a row created before the first phase transition landed (a fallback
/// insert by the claim path) is backfilled on the next beat instead of staying blank.
/// </param>
/// <param name="LibraryVersion">The library version the instance runs, or null to leave the registry's value alone.</param>
/// <param name="StaleThresholdSeconds">
/// How stale a peer's heartbeat row may be before this beat's opportunistic reap treats the peer
/// as dead. Derived from the writer's cadence (see <c>HeartbeatLivenessThreshold</c>); null keeps
/// the SQL default.
/// </param>
/// <docs>fundamentals/work-coordinator/configuration-reference</docs>
public sealed record HeartbeatRequest(
  Guid InstanceId,
  string ServiceName,
  string HostName,
  int ProcessId,
  JsonElement? Metadata = null,
  string? LifecyclePhase = null,
  string? LibraryVersion = null,
  int? StaleThresholdSeconds = null);
