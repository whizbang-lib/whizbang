// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Startup;

/// <summary>Whether a role currently has a valid holder.</summary>
/// <docs>proposals/duty-role-assignment</docs>
public enum RoleAssignmentState {
  /// <summary>A holder with an unexpired lease, registered and not evicted.</summary>
  Held,

  /// <summary>A holder is recorded but its assignment is no longer valid; the next vote voids it.</summary>
  Lapsed,

  /// <summary>Nobody holds the role.</summary>
  Vacant,
}

/// <summary>One role's assignment as the database reports it.</summary>
/// <param name="Role">The role.</param>
/// <param name="State">Held, lapsed or vacant.</param>
/// <param name="HolderInstanceId">The recorded holder, if any.</param>
/// <param name="Epoch">The current or last epoch.</param>
/// <param name="AssignedAt">When the current or last holder was assigned.</param>
/// <param name="RenewedAt">When its lease was last renewed.</param>
/// <param name="LeaseRemaining">Time left on a held lease, measured by the database.</param>
/// <param name="ElectionCount">How many assignments this role has had.</param>
/// <param name="VoidReason">Why a lapsed assignment is no longer valid.</param>
/// <param name="LastHolderInstanceId">Who held the role before the last vacancy.</param>
/// <param name="LastVacatedAt">When the role was last vacated.</param>
/// <param name="LastVacatedReason">Why: released, lapsed, evicted or unregistered.</param>
/// <param name="PendingWork">How much duty work is owed to the holder.</param>
/// <docs>proposals/duty-role-assignment</docs>
public sealed record RoleAssignmentSnapshot(
  string Role,
  RoleAssignmentState State,
  Guid? HolderInstanceId,
  long Epoch,
  DateTimeOffset? AssignedAt,
  DateTimeOffset? RenewedAt,
  TimeSpan? LeaseRemaining,
  long ElectionCount,
  string? VoidReason,
  Guid? LastHolderInstanceId,
  DateTimeOffset? LastVacatedAt,
  string? LastVacatedReason,
  long PendingWork) {
  /// <summary>When a newer-version instance asked the holder to drain, if one has.</summary>
  public DateTimeOffset? DrainRequestedAt { get; init; }

  /// <summary>Whether the backend the holder marked for its duty is running a statement now (the lease backstop).</summary>
  public bool DutyBackendActive { get; init; }
}

/// <summary>Reads every role's assignment, for health, metrics and operators.</summary>
/// <docs>proposals/duty-role-assignment</docs>
public interface IRoleAssignmentReader {
  /// <summary>One snapshot per role that has ever been voted for.</summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The snapshots, ordered by role.</returns>
  Task<IReadOnlyList<RoleAssignmentSnapshot>> ReadAssignmentsAsync(CancellationToken cancellationToken);
}
