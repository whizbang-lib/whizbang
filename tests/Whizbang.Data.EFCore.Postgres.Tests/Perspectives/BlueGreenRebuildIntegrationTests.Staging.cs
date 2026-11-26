// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The staging load of a blue-green rebuild end to end on the EF Core driver (#2248): the first pass copies its rows into
/// the shadow table with no insert, the shadow's plain indexes are built after the load and installed under the live
/// names, and the failure combinations leave the live table correct: a shadow left by a crash mid-load, a rebuild
/// abandoned mid-load, and a row the table refuses. The catch-up of a write committed during the load is the first test of
/// <see cref="BlueGreenRebuildIntegrationTests"/>, which runs with the staging load on, as every rebuild does by default.
/// </summary>
public partial class BlueGreenRebuildIntegrationTests {
  private const string PLAIN_INDEX = "ix_rebuild_balance_amount";

  [Test]
  public async Task Staging_CopiesTheFirstPassIntoTheShadow_WithItsPlainIndexesBuiltAfter_AndInstallsEveryIndexAsync() {
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
    var shadowIndexesDuringLoad = new List<long>();
    await using var sp = _services(
      async () => shadowIndexesDuringLoad.Add(await _scalarAsync<long>($"SELECT count(*) FROM pg_indexes WHERE tablename = '{TABLE}_bg'")),
      onFirstRun: _ => Task.CompletedTask);
    for (var i = 0; i < streams.Length; i++) {
      await _appendAsync(sp, new RebuildCreditedEvent { StreamId = streams[i], Amount = 10m * (i + 1) });
    }
    await _registerTableAsync();
    await _execAsync($"CREATE INDEX {PLAIN_INDEX} ON {TABLE} ((data->>'Balance'))");
    await _stampCommitSequencesAsync();
    var indexesBefore = await _indexNamesAsync(TABLE);
    var keyAndUniqueIndexes = await _scalarAsync<long>(
      $"SELECT count(*) FROM pg_index WHERE indrelid = '{TABLE}'::regclass AND (indisprimary OR indisunique)");
    using var log = new NpgsqlStatementLog(ConnectionString);

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    await Assert.That(keyAndUniqueIndexes).IsLessThan(indexesBefore.Count).Because("the control: the live table has a plain index to defer");
    await Assert.That(await _liveBalancesAsync()).IsEquivalentTo(new Dictionary<Guid, decimal> {
      [streams[0]] = 10m,
      [streams[1]] = 20m,
      [streams[2]] = 30m,
    });
    await Assert.That(log.Matching("INSERT", TABLE + "_bg")).IsEmpty()
      .Because("every row of the first pass reached the shadow by COPY, and nothing changed after it to catch up");
    await Assert.That(shadowIndexesDuringLoad.Distinct()).IsEquivalentTo([keyAndUniqueIndexes])
      .Because("the plain indexes are dropped for the load; the key and the unique ones stay, so the load refuses what the per-row write refuses");
    await Assert.That(await _indexNamesAsync(TABLE)).IsEquivalentTo(indexesBefore)
      .Because("the index built after the load is installed under the live index's name, so the schema pass finds it");
  }

  [Test]
  public async Task Staging_SwitchedOff_WritesTheFirstPassRowByRowAsync() {
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid() };
    await using var sp = _services(() => Task.CompletedTask, onFirstRun: _ => Task.CompletedTask,
      options: new BlueGreenRebuildOptions { StagingLoad = false });
    foreach (var stream in streams) {
      await _appendAsync(sp, new RebuildCreditedEvent { StreamId = stream, Amount = 5m });
    }
    await _registerTableAsync();
    using var log = new NpgsqlStatementLog(ConnectionString);

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    await Assert.That(log.Matching("INSERT", TABLE + "_bg").Count).IsGreaterThanOrEqualTo(2)
      .Because("the positive control: without the staging load each row is inserted on its own, which the log does see");
    await Assert.That(await _liveBalancesAsync()).IsEquivalentTo(new Dictionary<Guid, decimal> { [streams[0]] = 5m, [streams[1]] = 5m });
  }

  [Test]
  public async Task Staging_AShadowLeftByACrashMidLoad_IsReplaced_AndTheRebuildInstallsEveryIndexAsync() {
    var stream = Guid.NewGuid();
    await using var sp = _services(() => Task.CompletedTask, onFirstRun: _ => Task.CompletedTask);
    await _appendAsync(sp, new RebuildCreditedEvent { StreamId = stream, Amount = 42m });
    await _registerTableAsync();
    await _execAsync($"CREATE INDEX {PLAIN_INDEX} ON {TABLE} ((data->>'Balance'))");
    var indexesBefore = await _indexNamesAsync(TABLE);
    // What a process killed mid-load leaves: the shadow without its plain index, and some rows copied.
    var junk = Guid.NewGuid();
    await _seedStaleLiveRowsAsync([junk]);
    await _execAsync($"""
      CREATE TABLE {TABLE}_bg (LIKE {TABLE} INCLUDING ALL EXCLUDING INDEXES);
      INSERT INTO {TABLE}_bg SELECT * FROM {TABLE};
      DELETE FROM {TABLE} WHERE id = '{junk}';
      """);

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsTrue().Because(result.Error ?? "");
    await Assert.That(await _liveBalancesAsync()).IsEquivalentTo(new Dictionary<Guid, decimal> { [stream] = 42m })
      .Because("the leftover's rows are gone with it; only the replayed rows are installed");
    await Assert.That(await _indexNamesAsync(TABLE)).IsEquivalentTo(indexesBefore);
  }

  [Test]
  public async Task Staging_AnAbandonedRebuild_DropsTheShadow_AndLeavesTheLiveTableAsItWasAsync() {
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid() };
    using var cts = new CancellationTokenSource();
    await using var sp = _services(() => Task.CompletedTask, onFirstRun: _ => {
      cts.Cancel();
      return Task.CompletedTask;
    });
    foreach (var stream in streams) {
      await _appendAsync(sp, new RebuildCreditedEvent { StreamId = stream, Amount = 5m });
    }
    await _seedStaleLiveRowsAsync(streams);
    await _registerTableAsync();
    var stale = await _liveBalancesAsync();

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildBlueGreenAsync(PERSPECTIVE, cts.Token);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(await _scalarAsync<bool>($"SELECT to_regclass('{TABLE}_bg') IS NULL")).IsTrue()
      .Because("an abandoned rebuild drops its half-loaded shadow");
    await Assert.That(await _liveBalancesAsync()).IsEquivalentTo(stale);
  }

  [Test]
  public async Task Staging_ARowTheTableRefuses_FailsOnlyItsStream_AndTheOthersAreInstalledAsync() {
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
    await using var sp = _services(() => Task.CompletedTask, onFirstRun: _ => Task.CompletedTask);
    await _appendAsync(sp, new RebuildCreditedEvent { StreamId = streams[0], Amount = 100m });
    await _appendAsync(sp, new RebuildCreditedEvent { StreamId = streams[1], Amount = 500m });
    await _appendAsync(sp, new RebuildCreditedEvent { StreamId = streams[2], Amount = 25m });
    await _registerTableAsync();
    await _execAsync($"ALTER TABLE {TABLE} ADD CONSTRAINT ck_rebuild_balance_cap CHECK ((data->>'Balance')::numeric < 400)");
    await _stampCommitSequencesAsync();

    var result = await sp.GetRequiredService<IPerspectiveRebuilder>().RebuildBlueGreenAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).Contains(streams[1].ToString());
    await Assert.That(result.StreamsProcessed).IsEqualTo(2);
    await Assert.That(await _liveBalancesAsync()).IsEquivalentTo(new Dictionary<Guid, decimal> { [streams[0]] = 100m, [streams[2]] = 25m })
      .Because("the copy the constraint refused was written again by statements that isolated the one row");
  }

  [Test]
  public async Task Store_InAStagingLoad_HoldsNewRows_ReadsThemAbsentWithoutAQuery_AndLoadsAHeldRowBeforeItIsReadOrWrittenAgainAsync() {
    await _execAsync($"CREATE TABLE {TABLE}_bg (LIKE {TABLE} INCLUDING ALL)");
    var (read, rewritten, purged, never, late) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<RebuildBalanceModel>(context, TABLE);
    var load = new PerspectiveStagingLoad(TABLE + "_bg", 100);

    using (PerspectiveTableRedirect.Begin(TABLE, TABLE + "_bg"))
    using (PerspectiveStagingLoad.Begin(load)) {
      using var log = new NpgsqlStatementLog(ConnectionString);
      await Assert.That((await store.ReadForApplyAsync(never)).Version.State).IsEqualTo(PerspectiveRowVersionState.Absent);
      await Assert.That(await store.GetByStreamIdAsync(never)).IsNull();
      await Assert.That(await store.GetMetadataByStreamIdAsync(never)).IsNull();
      await store.PurgeAsync(never);
      await store.UpsertAsync(read, new RebuildBalanceModel { Id = read, Balance = 1m });
      await store.UpsertAsync(rewritten, new RebuildBalanceModel { Id = rewritten, Balance = 2m });
      await store.UpsertAsync(purged, new RebuildBalanceModel { Id = purged, Balance = 3m });
      await store.PurgeAsync(purged);
      await Assert.That(log.Statements).IsEmpty()
        .Because("an absent row is known absent, and a held row is written by nothing until it is loaded");
      await Assert.That(load.StagedCount).IsEqualTo(2);

      await store.UpsertAsync(rewritten, new RebuildBalanceModel { Id = rewritten, Balance = 20m });
      await Assert.That(load.StagedCount).IsEqualTo(0).Because("a held row is loaded before it is written again");
      await store.UpsertAsync(late, new RebuildBalanceModel { Id = late, Balance = 4m });
      await Assert.That((await store.GetByStreamIdAsync(late))!.Balance).IsEqualTo(4m)
        .Because("a held row is loaded before it is read, so the read finds it");
      await store.UpsertAsync(late, new RebuildBalanceModel { Id = late, Balance = 40m });
      await Assert.That(await store.GetMetadataByStreamIdAsync(read)).IsNotNull();
      await Assert.That((await store.ReadForApplyAsync(read)).Version.State).IsEqualTo(PerspectiveRowVersionState.Present);
      await store.PurgeAsync(read);
    }

    await Assert.That(await _balancesAsync(TABLE + "_bg")).IsEquivalentTo(new Dictionary<Guid, decimal> { [rewritten] = 20m, [late] = 40m })
      .Because("the second write of a loaded row is the per-row upsert; the purged rows are in neither the load nor the table");
    await Assert.That(load.TakeRefused()).IsEmpty();
  }

  /// <summary>
  /// A batched read of rows in a redirected flow (#2513): the staging load answers it, every row absent and no statement
  /// sent, while the load holds none of them; a row it holds, no staging load (a catch-up pass) or a redirect of another
  /// table leaves each row to its own read.
  /// </summary>
  [Test]
  public async Task Store_InARedirectedFlow_AnswersABatchedRowRead_FromTheStagingLoadOnly_WhileItHoldsNoneOfTheRowsAsync() {
    await _execAsync($"CREATE TABLE {TABLE}_bg (LIKE {TABLE} INCLUDING ALL)");
    var (absent, held) = (Guid.NewGuid(), Guid.NewGuid());
    await using var context = CreateDbContext();
    var store = new EFCorePostgresPerspectiveStore<RebuildBalanceModel>(context, TABLE);
    var load = new PerspectiveStagingLoad(TABLE + "_bg", 100);

    using (PerspectiveTableRedirect.Begin(TABLE, TABLE + "_bg")) {
      await Assert.That(await store.ReadForApplyManyAsync([absent])).IsNull().Because("a catch-up pass has no staging load");
      using (PerspectiveStagingLoad.Begin(load)) {
        using var log = new NpgsqlStatementLog(ConnectionString);
        await Assert.That(await store.ReadForApplyManyAsync([absent])).IsEmpty()
          .Because("the load has written none of the rows, so every one is absent");
        await Assert.That(log.Statements).IsEmpty();
        await store.UpsertAsync(held, new RebuildBalanceModel { Id = held, Balance = 1m });
        await Assert.That(await store.ReadForApplyManyAsync([absent, held])).IsNull()
          .Because("a row the load holds is read on its own, which loads it first");
      }
    }
    using (PerspectiveTableRedirect.Begin("wh_per_some_other_table", "wh_per_some_other_table_bg")) {
      await Assert.That(await store.ReadForApplyManyAsync([absent])).IsNull()
        .Because("a redirect of another table leaves this store's flow to its own reads");
    }
  }

  /// <summary>
  /// Stamps every event's commit sequence, as the stamper does once events are committed. An unstamped event always
  /// counts as written during the rebuild, so without this every stream would be caught up again after the load.
  /// </summary>
  private Task _stampCommitSequencesAsync() => _execAsync("""
    UPDATE wh_event_store es SET commit_sequence = s.seq
    FROM (SELECT event_id, row_number() OVER (ORDER BY event_id) AS seq FROM wh_event_store) s
    WHERE es.event_id = s.event_id
    """);

  private async Task<List<string>> _indexNamesAsync(string table) {
    await using var conn = await _openAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT indexname FROM pg_indexes WHERE tablename = '{table}' ORDER BY indexname";
    var names = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      names.Add(reader.GetString(0));
    }
    return names;
  }
}
