using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Startup;

/// <summary>One live instance, as the status surface's fleet section reports it.</summary>
/// <param name="InstanceId">The instance's id — the key that finds the responder's own row.</param>
/// <param name="ServiceName">The service the instance belongs to.</param>
/// <param name="HostName">Where it runs.</param>
/// <param name="LastHeartbeatAt">
/// When it was last heard from. Every fleet row is only as current as this — a reader must judge
/// freshness per row, or an instance that died thirty seconds ago reads as healthy.
/// </param>
/// <param name="Capabilities">
/// What the instance currently holds, from the recorded holdings — "which instance is the migrator
/// right now" as a join, not a fan-out. Derived state: the lock decides, the row reports.
/// </param>
/// <docs>proposals/startup-pipeline#status</docs>
/// <param name="LifecyclePhase">The phase the instance last recorded for itself, when it has.</param>
/// <param name="LibraryVersion">The library version its binary runs, when recorded — during a
/// mixed-version rollout this is the first question anyone asks.</param>
/// <param name="Evicted">Whether the instance is tombstoned — refused at heartbeat, capability
/// acquisition and claims. An evicted peer no longer counts for anything, handshakes included.</param>
public sealed record FleetInstanceStatus(
  Guid InstanceId, string ServiceName, string HostName, DateTimeOffset LastHeartbeatAt,
  IReadOnlyList<string> Capabilities, string? LifecyclePhase = null, string? LibraryVersion = null,
  bool Evicted = false);

/// <summary>
/// The fleet section of a status response: either the live instances from the database, or an
/// honest statement of why they cannot be seen. Never an empty list standing in for "unreachable" —
/// "no other instances" and "cannot see the other instances" mean opposite things during an incident.
/// </summary>
/// <param name="Available">Whether the fleet could be read.</param>
/// <param name="UnavailableReason">Why not, when it could not.</param>
/// <param name="Instances">The live instances, when it could.</param>
/// <docs>proposals/startup-pipeline#status</docs>
public sealed record FleetStatusReport(
  bool Available, string? UnavailableReason, IReadOnlyList<FleetInstanceStatus> Instances) {

  /// <summary>An unavailable fleet with the stated reason.</summary>
  public static FleetStatusReport Unavailable(string reason) => new(false, reason, []);
}

/// <summary>
/// Reads the fleet for the status surface. Supplied by the storage driver — the fleet lives in the
/// database, and only a driver knows how to reach it. When none is registered, the surface reports
/// the fleet section unavailable with that as the reason, which is the honest answer.
/// </summary>
/// <docs>proposals/startup-pipeline#status</docs>
public interface IStartupFleetStatusSource {
  /// <summary>Reads the live instances. Implementations should throw on failure — the surface
  /// translates the failure into an unavailable fleet section rather than an error response.</summary>
  Task<IReadOnlyList<FleetInstanceStatus>> GetFleetAsync(CancellationToken cancellationToken);
}
