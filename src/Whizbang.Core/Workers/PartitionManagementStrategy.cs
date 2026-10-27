// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using Whizbang.Core.Capabilities;

namespace Whizbang.Core.Workers;

/// <summary>
/// Who creates the partitions of the framework's partitioned tables
/// (<c>Whizbang:Workers:Maintenance:Partitioning:Strategy</c>), resolved by
/// <see cref="PartitionManagementStrategyResolver"/>. Whichever creates them, the built-in manager drops expired
/// partitions, because a drop must first carry the signals a live instance has not consumed.
/// </summary>
/// <docs>operations/infrastructure/partitioned-ledgers#pg-partman</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PartitionManagementStrategyTests.cs</tests>
public enum PartitionManagementStrategy {
  /// <summary>
  /// The default. pg_partman creates partitions where it is already Ready on every database probed; otherwise the
  /// built-in manager does, and the maintenance cycle says why. Auto never installs pg_partman.
  /// </summary>
  Auto,

  /// <summary>The built-in manager creates and drops partitions, even where pg_partman is Ready.</summary>
  BuiltIn,

  /// <summary>
  /// pg_partman creates partitions ahead: each table is registered with <c>create_parent</c> once and maintained by
  /// <c>run_maintenance</c> each cycle. Chosen explicitly, it requires pg_partman, so a server that cannot provide it
  /// stops start with the capability verdict's fix, and a cycle that cannot reach it fails.
  /// </summary>
  [RequiresPostgresExtension("pg_partman", Feature = "Partition management")]
  PgPartman,
}

/// <summary>What the partition maintenance cycle does this time: whether pg_partman creates partitions, and why.</summary>
/// <param name="UsePgPartman">Whether each table gets a pg_partman pass before the built-in pass.</param>
/// <param name="BuiltInOnFailure">
/// Whether a pg_partman pass that fails is reported and the built-in manager keeps the table for the cycle
/// (<see cref="PartitionManagementStrategy.Auto"/>), rather than failing the cycle.
/// </param>
/// <param name="Reason">Why, in a phrase the cycle reports.</param>
/// <docs>operations/infrastructure/partitioned-ledgers#pg-partman</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PartitionManagementStrategyTests.cs</tests>
public readonly record struct PartitionManagementPlan(bool UsePgPartman, bool BuiltInOnFailure, string Reason);

/// <summary>
/// Resolves <see cref="PartitionManagementStrategy"/> against pg_partman's capability state.
/// </summary>
/// <remarks>
/// <list type="table">
///   <listheader><term>Strategy</term><description>Resolves to</description></listheader>
///   <item><term>Auto</term><description>pg_partman when Ready, the built-in manager otherwise, naming the state. Never installs pg_partman.</description></item>
///   <item><term>BuiltIn</term><description>The built-in manager.</description></item>
///   <item><term>PgPartman</term><description>pg_partman, whatever its state; the capability verdict stops start where it cannot be had.</description></item>
/// </list>
/// </remarks>
/// <docs>operations/infrastructure/partitioned-ledgers#pg-partman</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PartitionManagementStrategyTests.cs</tests>
public static class PartitionManagementStrategyResolver {
  private const string STRATEGY_KEY = "Whizbang:Workers:Maintenance:Partitioning:Strategy";
  private const string PG_PARTMAN_CREATES = "pg_partman creates partitions ahead; the built-in manager still drops expired ones";
  private const string BUILT_IN_DOES_IT = "so the built-in manager creates and drops partitions";
  private static readonly CapabilityId _pgPartman = new(RequiresPostgresExtensionAttribute.PROVIDER, "pg_partman");

  /// <summary>The plan for one cycle.</summary>
  /// <param name="strategy">The configured strategy.</param>
  /// <param name="pgPartman">pg_partman's state (<see cref="PgPartmanState"/>).</param>
  /// <returns>Whether pg_partman creates partitions, whether a failed pass falls back, and why.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="strategy"/> is not a declared value.</exception>
  public static PartitionManagementPlan Resolve(PartitionManagementStrategy strategy, CapabilityState pgPartman) =>
    strategy switch {
      PartitionManagementStrategy.Auto => _auto(pgPartman),
      PartitionManagementStrategy.BuiltIn => new(false, false, "BuiltIn: the built-in manager creates and drops partitions"),
      PartitionManagementStrategy.PgPartman => new(true, false, pgPartman == CapabilityState.Ready
        ? "PgPartman: " + PG_PARTMAN_CREATES
        : $"PgPartman: {PG_PARTMAN_CREATES} (pg_partman reads {pgPartman}; a cycle that cannot reach it fails)"),
      _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "not a PartitionManagementStrategy"),
    };

  private static PartitionManagementPlan _auto(CapabilityState pgPartman) => pgPartman switch {
    CapabilityState.Ready => new(true, true, "Auto: pg_partman is Ready, so " + PG_PARTMAN_CREATES),
    CapabilityState.Provisionable => new(false, true,
      "Auto: pg_partman is available but not installed, and Auto never installs it, " + BUILT_IN_DOES_IT
      + "; set " + STRATEGY_KEY + " = PgPartman to install and use it"),
    _ => new(false, true, $"Auto: pg_partman is {pgPartman}, {BUILT_IN_DOES_IT}"),
  };

  /// <summary>
  /// pg_partman's state across every database the registry probes: Ready only when every one says so, otherwise the
  /// first state that is not; Unknown with no registry or no status for it.
  /// </summary>
  /// <param name="registry">The capability registry, or null when the host registers none.</param>
  /// <returns>The state the strategy resolves against.</returns>
  public static CapabilityState PgPartmanState(ICapabilityRegistry? registry) {
    var states = registry?.Statuses.Where(s => s.Descriptor.Id == _pgPartman).Select(s => s.State).ToList() ?? [];
    return states.Count == 0
      ? CapabilityState.Unknown
      : states.FirstOrDefault(state => state != CapabilityState.Ready, CapabilityState.Ready);
  }
}
