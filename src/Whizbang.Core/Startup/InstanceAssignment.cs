// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Startup;

/// <summary>What an instance has been given to do (#2595), derived from the tables that decide it.</summary>
/// <docs>operations/startup/startup-status#instance-assignments</docs>
public enum InstanceAssignmentKind {
  /// <summary>Registered, and not yet eligible for work: it is still starting.</summary>
  Starting,

  /// <summary>Eligible, owns a share of the partitions, and holds no role.</summary>
  StandardWorker,

  /// <summary>Eligible, owns a share of the partitions, and holds at least one role: a standard worker as well.</summary>
  RoleHolder,

  /// <summary>
  /// Eligible for work but owning no partition: no map gives it a share yet. Readiness does not wait for a share, so
  /// this is how a missing one (for example while the partition assigner is unheld) is seen.
  /// </summary>
  AwaitingShare,

  /// <summary>Leaving or held back: pausing, paused, standing by, resuming, stopping, stopped, faulted or halted.</summary>
  Draining,

  /// <summary>An older release in a service that claims by the partition map, which gives it no partition.</summary>
  Ineligible,

  /// <summary>Tombstoned: refused at heartbeat, acquisition and claims.</summary>
  Evicted,
}

/// <summary>One instance's assignment, as one fact (#2595).</summary>
/// <param name="InstanceId">The instance.</param>
/// <param name="ServiceName">Its service.</param>
/// <param name="HostName">Where it runs.</param>
/// <param name="LibraryVersion">The library version it runs, when recorded.</param>
/// <param name="Eligible">Whether its recorded stage and phase make it eligible for work.</param>
/// <param name="IsLive">Whether the one liveness rule calls it live.</param>
/// <param name="Roles">The roles it validly holds, in role order.</param>
/// <param name="OwnedPartitions">How many of the partitions it owns.</param>
/// <param name="MapRevision">The revision of the published partition map, when one is published.</param>
/// <param name="Kind">What it has been given to do.</param>
/// <docs>operations/startup/startup-status#instance-assignments</docs>
public sealed record InstanceAssignment(
  Guid InstanceId, string ServiceName, string HostName, string? LibraryVersion, bool Eligible, bool IsLive,
  IReadOnlyList<string> Roles, int OwnedPartitions, long? MapRevision, InstanceAssignmentKind Kind);

/// <summary>Reads every registered instance's assignment (#2595). Supplied by the storage driver.</summary>
/// <docs>operations/startup/startup-status#instance-assignments</docs>
public interface IInstanceAssignmentReader {
  /// <summary>One assignment per registered instance, ordered by instance id.</summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The assignments.</returns>
  Task<IReadOnlyList<InstanceAssignment>> ReadAssignmentsAsync(CancellationToken cancellationToken);
}
