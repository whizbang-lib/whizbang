// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Every claim function tests a partition through wh_partition_in_share, the one predicate that reads the published
/// partition map (#2558). A partition test written as a bare modulo would claim by the members' residue while the
/// assigner hands partitions out by the map, so two instances could each take the other's work. This reads the last
/// word of each claim function, as the database ends up defining it, and fails on any bare modulo against the rank.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#incremental-map</docs>
[Category("Migrations")]
[Category("Shard4")]
public partial class ClaimsReadThePartitionMapGuardTests {
  /// <summary>The functions that decide which partitions an instance claims.</summary>
  private static readonly string[] _claimFunctions = [
    "claim_work",
    "claim_orphaned_outbox",
    "claim_orphaned_inbox",
    "claim_orphaned_perspective_events",
    "wh_claim_multi_stream_work",
  ];

  [GeneratedRegex(@"%\s*\(?\s*(p_active_instance_count|v_count)\b")]
  private static partial Regex _bareModulo();

  /// <summary>The body of <paramref name="function"/> in the given migrations' last definition of it.</summary>
  private static string _lastWordOf(string function, Func<string, bool> migrations) {
    var sql = new Whizbang.Data.Postgres.PostgresMigrationProvider(
        typeof(Whizbang.Data.Postgres.PostgresMigrationProvider).Assembly, "__SCHEMA__")
      .GetMigrations()
      .Where(m => migrations(m.Name) && m.Sql.Contains($"FUNCTION __SCHEMA__.{function}(", StringComparison.Ordinal))
      .OrderBy(m => m.Name, StringComparer.Ordinal)
      .Last().Sql;
    var at = sql.IndexOf($"CREATE OR REPLACE FUNCTION __SCHEMA__.{function}(", StringComparison.Ordinal);
    var end = sql.IndexOf("\n$$", at, StringComparison.Ordinal);
    return sql[at..end];
  }

  [Test]
  public async Task EveryClaimFunction_TestsItsPartitionsThroughTheMapPredicateAsync() {
    foreach (var function in _claimFunctions) {
      var body = _lastWordOf(function, _ => true);

      await Assert.That(_bareModulo().IsMatch(body)).IsFalse()
        .Because($"{function} must test a partition through wh_partition_in_share, never a bare modulo against the rank");
      await Assert.That(body).Contains("wh_partition_in_share(")
        .Because($"{function} decides ownership by partition, so it must read the published map");
    }
  }

  [Test]
  public async Task TheGuard_FindsTheBareModulo_InTheDefinitionsBeforeTheMapAsync() {
    // The positive control: before migration 566, every one of these tested its partitions by a bare modulo. A guard that
    // cannot find it there proves nothing by finding nothing now.
    foreach (var function in _claimFunctions) {
      var body = _lastWordOf(function, name => string.CompareOrdinal(name, "566") < 0);

      await Assert.That(_bareModulo().IsMatch(body)).IsTrue()
        .Because($"{function}'s definition before 566 ranks by modulo, which this guard must see");
    }
  }
}
