// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2261: what the derived completion gauges read, against the real tables. An origin's latest commit sequence is kept
/// from its checkpoints beside its name, and read with its seal; the buckets each instance opened and closed are added to
/// a per-minute tally the service shares, pruned in the same call, and summed over a window.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/401_IntegrityCompletionIsReportedDirectly.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#derived-completion-meters</docs>
[Category("Integration")]
[Category("Shard5")]
public class IntegrityCompletionSqlTests : EFCoreTestBase {
  private const int MAX_ATTEMPTS = 8;

  // Fixed instants, not the host clock: every call here takes the caller's clock.
  private static readonly DateTimeOffset _t0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

  private readonly List<WorkCoordinationDbContext> _contexts = [];

  // ── the origin's latest commit sequence ──────────────────────────────────────────────────────

  [Test]
  public async Task TheSeals_CarryTheLatestCommitSequenceTheOriginsCheckpointsAnnouncedAsync() {
    var coordinator = _coordinator();
    var checkpointed = TrackedGuid.New().Value;
    var neverCheckpointed = TrackedGuid.New().Value;
    var announcedNothing = TrackedGuid.New().Value;
    await coordinator.IntegrityRecordOriginCheckpointAsync(checkpointed, "orders", null, _t0, latestCommitSequence: 900);
    await coordinator.IntegrityRecordOriginCheckpointAsync(checkpointed, "orders", null, _t0.AddMinutes(1), latestCommitSequence: 700);
    await coordinator.IntegrityRecordOriginCheckpointAsync(announcedNothing, "billing", null, _t0);
    await coordinator.AdvanceIntegritySealAsync(checkpointed, 451);
    await coordinator.AdvanceIntegritySealAsync(neverCheckpointed, 10);
    await coordinator.AdvanceIntegritySealAsync(announcedNothing, 3);

    var seals = (await coordinator.GetIntegrityLedgerSummaryAsync(MAX_ATTEMPTS)).Seals;

    await Assert.That(seals.Single(s => s.OriginServiceId == checkpointed).LatestCommitSequence).IsEqualTo(900L)
      .Because("the highest watermark announced is kept; a checkpoint delivered late does not move it back");
    await Assert.That(seals.Single(s => s.OriginServiceId == checkpointed).SealedThrough).IsEqualTo(451L);
    await Assert.That(seals.Single(s => s.OriginServiceId == neverCheckpointed).LatestCommitSequence).IsNull();
    await Assert.That(seals.Single(s => s.OriginServiceId == announcedNothing).LatestCommitSequence).IsNull()
      .Because("a checkpoint recorded without a watermark leaves it unknown");
  }

  // ── the shared tally ─────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task TheTally_AddsPerMinute_AndIsSummedOverTheWindowAsync() {
    var coordinator = _coordinator();

    await Assert.That(await coordinator.IntegrityAddLedgerTallyAsync(_t0.AddSeconds(10), 3, 1, _t0.AddHours(-2))).IsTrue();
    await Assert.That(await coordinator.IntegrityAddLedgerTallyAsync(_t0.AddSeconds(50), 2, 4, _t0.AddHours(-2))).IsTrue();
    await Assert.That(await coordinator.IntegrityAddLedgerTallyAsync(_t0.AddMinutes(-90), 100, 100, _t0.AddHours(-2))).IsTrue();

    var window = await coordinator.GetIntegrityLedgerTallyAsync(_t0.AddHours(-1));
    var everything = await coordinator.GetIntegrityLedgerTallyAsync(_t0.AddHours(-3));

    await Assert.That(window).IsEqualTo(new IntegrityLedgerTally(Opened: 5, Closed: 5))
      .Because("two instances' counts in the same minute add up, and a minute before the window is outside it");
    await Assert.That(everything).IsEqualTo(new IntegrityLedgerTally(Opened: 105, Closed: 105));
    await using var conn = await _openAsync();
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_integrity_ledger_tally")).IsEqualTo(2L)
      .Because("one row per minute, however many instances write to it");
  }

  [Test]
  public async Task TheTally_PrunesMinutesOlderThanItKeeps_InTheSameCallAsync() {
    var coordinator = _coordinator();
    _ = await coordinator.IntegrityAddLedgerTallyAsync(_t0.AddHours(-5), 9, 9, _t0.AddHours(-7));

    _ = await coordinator.IntegrityAddLedgerTallyAsync(_t0, 1, 0, _t0.AddHours(-2));

    await Assert.That(await coordinator.GetIntegrityLedgerTallyAsync(_t0.AddDays(-1)))
      .IsEqualTo(new IntegrityLedgerTally(Opened: 1, Closed: 0))
      .Because("the tally holds the window it is read over and no more, so it stays a handful of rows");
  }

  [Test]
  public async Task AnEmptyTally_ReadsZeroAsync() {
    await Assert.That(await _coordinator().GetIntegrityLedgerTallyAsync(_t0.AddHours(-1)))
      .IsEqualTo(new IntegrityLedgerTally(Opened: 0, Closed: 0))
      .Because("a window with nothing opened or closed is a real reading, not a missing one");
  }

  [Test]
  public async Task ATallyThatCannotBeReached_IsNotWritten_AndNotReadAsync() {
    var coordinator = _coordinator();
    await using var conn = await _openAsync();
    await _execAsync(conn, "DROP TABLE wh_integrity_ledger_tally CASCADE");

    await Assert.That(await coordinator.IntegrityAddLedgerTallyAsync(_t0, 1, 1, _t0.AddHours(-2))).IsFalse()
      .Because("the caller keeps its counts for the next tick when the write did not land");
    await Assert.That(await coordinator.GetIntegrityLedgerTallyAsync(_t0.AddHours(-1))).IsNull()
      .Because("a failed read reports nothing; a zero would read as no progress");
  }

  [Test]
  public async Task ACanceledTally_PropagatesTheCancellationAsync() {
    var coordinator = _coordinator();
    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();

    await Assert.That(async () => await coordinator.IntegrityAddLedgerTallyAsync(_t0, 1, 1, _t0, canceled.Token))
      .Throws<OperationCanceledException>();
    await Assert.That(async () => await coordinator.GetIntegrityLedgerTallyAsync(_t0, canceled.Token))
      .Throws<OperationCanceledException>();
  }

  // ── the reverse ──────────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// 401's reverse drops what 401 added: the tally (its minutes are lost, which is why 401 is lossy) and the kept
  /// watermark, so a rollback leaves an older release the tables it was built against.
  /// </summary>
  [Test]
  public async Task TheReverse_DropsTheTallyAndTheWatermarkAsync() {
    await using var conn = await _openAsync();
    var down = typeof(Whizbang.Data.Postgres.PostgresMigrationProvider).Assembly.GetManifestResourceStream(
      "Whizbang.Data.Postgres.MigrationReversals.401_IntegrityCompletionIsReportedDirectly.down.sql")
      ?? throw new InvalidOperationException("401 ships its reverse");
    using var reader = new StreamReader(down);
    await _execAsync(conn, Whizbang.Data.Postgres.MigrationConstants.Apply(
      (await reader.ReadToEndAsync()).Replace("__SCHEMA__", "public", StringComparison.Ordinal)));

    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM pg_class WHERE relname = 'wh_integrity_ledger_tally'"))
      .IsEqualTo(0L);
    await Assert.That(await _scalarAsync<long>(conn,
      "SELECT count(*) FROM information_schema.columns WHERE table_name = 'wh_integrity_known_origins' AND column_name = 'last_commit_sequence'"))
      .IsEqualTo(0L);
    foreach (var added in new[] { "wh_integrity_ledger_tally_add", "wh_integrity_ledger_tally_since" }) {
      await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM pg_proc WHERE proname = $1", added)).IsEqualTo(0L)
        .Because($"{added} is 401's and goes with it");
    }
  }

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator() {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, params object[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var arg in args) {
      cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
