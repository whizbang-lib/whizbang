// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Meters for role assignment (#966): how often roles are won, handed off (and why), lost and
/// released, how many this instance holds, and what happened to owed duty work.
/// </summary>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/RoleAssignmentMetricsTests.cs</tests>
public sealed class RoleAssignmentMetrics {
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>The meter name.</summary>
  public const string METER_NAME = "Whizbang.Roles";

  /// <summary>Tag naming the role.</summary>
  public const string ROLE_TAG = "role";

  /// <summary>Tag naming why a hand-off happened (released, lapsed, evicted, unregistered).</summary>
  public const string REASON_TAG = "reason";

  /// <summary>Tag naming what happened to a piece of owed work.</summary>
  public const string OUTCOME_TAG = "outcome";

  /// <summary>Owed work ran and is done.</summary>
  public const string OUTCOME_COMPLETED = "completed";

  /// <summary>Owed work ran and stays owed: it failed, or was owed again while it ran.</summary>
  public const string OUTCOME_LEFT_OWED = "left_owed";

  /// <summary>Owed work ran but the grant was fenced before it could be marked done.</summary>
  public const string OUTCOME_FENCED = "fenced";
#pragma warning restore CA1707

  /// <summary>Creates the meters.</summary>
  /// <param name="whizbangMetrics">The framework's meter factory holder.</param>
  public RoleAssignmentMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory.Create(METER_NAME);
    Elections = meter.CreatePassiveCounter<long>("whizbang.roles.elections",
      description: "Roles this instance won at a new epoch");
    Handoffs = meter.CreatePassiveCounter<long>("whizbang.roles.handoffs",
      description: "Roles this instance took over from a previous holder, by why the previous holder stopped");
    Lost = meter.CreatePassiveCounter<long>("whizbang.roles.lost",
      description: "Roles this instance held and lost without releasing them (lapsed, evicted, taken over)");
    Released = meter.CreatePassiveCounter<long>("whizbang.roles.released",
      description: "Roles this instance released cleanly");
    Held = meter.CreatePassiveUpDownCounter<long>("whizbang.roles.held",
      description: "Roles this instance holds right now");
    WorkRuns = meter.CreatePassiveCounter<long>("whizbang.roles.work_runs",
      description: "Owed duty work run by this instance as holder, by outcome");
    Drains = meter.CreatePassiveCounter<long>("whizbang.roles.drains",
      description: "Roles this instance released because a newer-version instance asked it to drain");
    StalledWithoutSuccessor = meter.CreatePassiveCounter<long>("whizbang.roles.stalled_without_successor",
      description: "Reads of the role assignments that found a holder stalled past its progress window and kept only because no peer can take its role");
  }

  /// <summary>Roles won at a new epoch.</summary>
  public PassiveCounter<long> Elections { get; }

  /// <summary>Take-overs from a previous holder, tagged with the reason.</summary>
  public PassiveCounter<long> Handoffs { get; }

  /// <summary>Roles lost without a release.</summary>
  public PassiveCounter<long> Lost { get; }

  /// <summary>Clean releases.</summary>
  public PassiveCounter<long> Released { get; }

  /// <summary>Roles held now.</summary>
  public PassiveCounter<long> Held { get; }

  /// <summary>Owed work runs, tagged with the outcome.</summary>
  public PassiveCounter<long> WorkRuns { get; }

  /// <summary>Releases on a drain request.</summary>
  public PassiveCounter<long> Drains { get; }

  /// <summary>
  /// Reads that found a role's holder stalled with no peer to take over (#2593), tagged with the role. The holder keeps
  /// the role; restarting it is the operator's call.
  /// </summary>
  /// <docs>operations/startup/capabilities-and-duties#a-lapse-needs-a-successor</docs>
  public PassiveCounter<long> StalledWithoutSuccessor { get; }

  /// <summary>The role tag.</summary>
  /// <param name="role">The role.</param>
  /// <returns>The tag.</returns>
  public static KeyValuePair<string, object?> RoleTag(string role) => new(ROLE_TAG, role);
}
