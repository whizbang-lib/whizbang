// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Since migration 310 (#1999) the maintenance tasks live in <c>wh_perform_maintenance(p_ephemeral_reap_event_ids)</c>,
/// which the maintenance worker calls with the batch its destruction hook saw, and <c>perform_maintenance()</c> is a call
/// of it with no restriction. A later migration that redefines <c>perform_maintenance()</c> whole, copied from an older
/// definition, would leave the worker running the old tasks and every other caller running the new ones, with no error.
/// This fails that migration, and a <c>wh_perform_maintenance</c> that lost the restriction.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations</code-under-test>
/// <docs>fundamentals/events/ephemeral-events#reap-deletes-what-the-hook-saw</docs>
[Category("Migrations")]
[Category("Shard4")]
public class PerformMaintenanceDelegatesTests {
  private static string _newestDefinition(string function) {
    var corpus = new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "__SCHEMA__").GetMigrations();
    var marker = $"CREATE OR REPLACE FUNCTION __SCHEMA__.{function}(";
    MigrationScript? newest = null;
    foreach (var migration in corpus) {
      if (migration.Sql.Contains(marker, StringComparison.Ordinal)) {
        newest = migration;
      }
    }
    var sql = newest!.Sql;
    var start = sql.LastIndexOf(marker, StringComparison.Ordinal);
    var end = sql.IndexOf("$$ LANGUAGE", sql.IndexOf("$$", start, StringComparison.Ordinal) + 2, StringComparison.Ordinal);
    return sql[start..end];
  }

  [Test]
  public async Task PerformMaintenance_ItsNewestDefinition_OnlyCallsWhPerformMaintenanceAsync() {
    var body = _newestDefinition("perform_maintenance");

    await Assert.That(body).Contains("RETURN QUERY SELECT * FROM __SCHEMA__.wh_perform_maintenance(NULL::UUID[]);")
      .Because("the maintenance tasks belong in wh_perform_maintenance, which the worker calls; edit it there");
    await Assert.That(body).DoesNotContain("DELETE")
      .Because("a perform_maintenance that runs tasks of its own has been redefined whole from a definition older than 310");
  }

  [Test]
  public async Task WhPerformMaintenance_ItsNewestDefinition_RestrictsTheEphemeralReapToTheGivenIdsAsync() {
    var body = _newestDefinition("wh_perform_maintenance");

    await Assert.That(body).Contains("AND (p_ephemeral_reap_event_ids IS NULL OR eb.event_id = ANY(p_ephemeral_reap_event_ids))")
      .Because("without it the reap deletes bodies that became eligible after the hook's batch was read (#1999)");
  }

  [Test]
  public async Task TheScanner_FindsTheOlderFullDefinition_SoTheRuleIsNotVacuousAsync() {
    // The control: the same extraction on the definition before 310 sees the tasks, so a whole redefinition is caught.
    var corpus = new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "__SCHEMA__").GetMigrations();
    var sql = corpus.Single(m => m.Name.StartsWith("295_", StringComparison.Ordinal)).Sql;

    await Assert.That(sql[sql.IndexOf("CREATE OR REPLACE FUNCTION __SCHEMA__.perform_maintenance(", StringComparison.Ordinal)..]).Contains("DELETE");
  }
}
