// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The definitions the database ends up with, after every migration has applied in number order, carry every branch the
/// bundle's migrations wrote into them (#2554, #2555, #2558). Migrations apply in number order and each one replaces a
/// function whole, so a later migration copied from an earlier last word silently drops what the migrations in between
/// added: a claim function rewritten from its definition before the ownership table would lose its map-mode branch, and
/// one rewritten before the partition map would lose the map-aware legacy branch. Reading the source text of the files
/// cannot tell which definition wins; the installed one can, so this reads <c>pg_get_functiondef</c>.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/566_ClaimsDecideThroughTheOwnershipTable.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/567_StoresNameNoOwnerInMapMode.sql</code-under-test>
[Category("Migrations")]
[Category("Shard3")]
public class InstalledClaimDefinitionsCarryEveryBranchSqlTests : EFCoreTestBase {
  /// <summary>The functions that decide which partitions an instance claims.</summary>
  public static IEnumerable<string> ClaimFunctions() => [
    "claim_work",
    "claim_orphaned_outbox",
    "claim_orphaned_inbox",
    "claim_orphaned_perspective_events",
    "wh_claim_multi_stream_work",
  ];

  [Test]
  [MethodDataSource(nameof(ClaimFunctions))]
  public async Task EveryClaimFunction_AsInstalled_HasTheMapModeBranch_AndTheMapAwareLegacyBranchAsync(string function) {
    var definition = await _definitionAsync(function);

    await Assert.That(definition).Contains("wh_partition_ownership_by_map()")
      .Because($"{function} reads the service's switch (#2555)");
    await Assert.That(definition.Contains("wh_i_own(", StringComparison.Ordinal)
        || definition.Contains("wh_partitions_i_own(", StringComparison.Ordinal)).IsTrue()
      .Because($"{function} decides through the ownership table in map mode (#2555)");
    await Assert.That(definition).Contains("wh_partition_in_share(")
      .Because($"{function}'s legacy branch follows the published partition map (#2558)");
  }

  [Test]
  public async Task ThePublish_AsInstalled_StoresTheMap_AndWritesTheOwnershipTableAsync() {
    var definition = await _definitionAsync("wh_publish_partition_assignment");

    await Assert.That(definition).Contains("partition_owners").Because("the publish stores the partition map (#2558)");
    await Assert.That(definition).Contains("wh_partition_ownership").Because("the publish writes the ownership table (#2555)");
  }

  [Test]
  public async Task TheOutboxPaths_AsInstalled_KeepTheSequenceHold_AndTheMapModeBranchAsync() {
    var continuation = await _definitionAsync("wh_continue_outbox_streams");
    var store = await _definitionAsync("store_outbox_messages");

    await Assert.That(continuation).Contains("behind_hold").Because("559 cuts a stream at its first held row (#2542)");
    await Assert.That(continuation).Contains("wh_partition_ownership_by_map()").Because("566's map-mode branch (#2555)");
    await Assert.That(store).Contains("sequence_hold_until").Because("559 stores the hold its origin decided (#2542)");
    await Assert.That(store).Contains("wh_partition_ownership_by_map()").Because("567's map-mode branch (#2555)");
  }

  private async Task<string> _definitionAsync(string function) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var cmd = connection.CreateCommand();
    // Exactly one overload per framework function, in the schema the connection's search path resolves first.
    cmd.CommandText = """
      SELECT pg_get_functiondef(p.oid)
      FROM pg_proc p
      WHERE p.proname = @name AND p.pronamespace = to_regnamespace(current_schema())
      """;
    cmd.Parameters.AddWithValue("name", function);
    var definition = (string?)await cmd.ExecuteScalarAsync();
    await Assert.That(definition).IsNotNull().Because($"{function} is installed");
    return definition!;
  }
}
