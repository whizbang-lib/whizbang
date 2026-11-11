// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2262: an origin generation bump invalidates only what it changed. The generation moves only when a rewrite touched
/// the origin's own lane (the lane its manifests answer), it records the lowest position the rewrite touched, and a
/// consumer lowers its seal to that position when the seal is above it and keeps the seal otherwise. Before this, every
/// bump (one per maintenance batch, on any lane) reset every consumer's seal to 0.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/396_ASealFallsOnlyAsFarAsARewriteReached.sql</code-under-test>
[Category("Integration")]
[Category("Shard4")]
public class OriginRewriteFloorSqlTests : EFCoreTestBase {
  private static readonly Guid _localLane = Guid.Empty;

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _generationAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COALESCE((SELECT setting_value::bigint FROM wh_settings WHERE setting_key = 'integrity_origin_generation'), 0)";
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<long?> _recordedFloorAsync(NpgsqlConnection conn, long generation) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT rewritten_from FROM wh_integrity_origin_rewrites WHERE generation = @g";
    cmd.Parameters.AddWithValue("g", generation);
    return await cmd.ExecuteScalarAsync() is long floor ? floor : null;
  }

  private static async Task<long> _rewriteRowsAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM wh_integrity_origin_rewrites";
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task _setSettingAsync(NpgsqlConnection conn, string key, long value) =>
    await _executeAsync(conn, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
      VALUES (@k, @v, 'integer', 'test')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """, ("k", key), ("v", value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

  /// <summary>Runs a rewrite's refold calls and the bump in ONE transaction, as every rewrite path does.</summary>
  private static async Task _rewriteAsync(NpgsqlConnection conn, params (Guid Lane, long Min, long Max)[] ranges) {
    await using var tx = await conn.BeginTransactionAsync();
    foreach (var (lane, min, max) in ranges) {
      await _executeAsync(conn, "SELECT _wh_refold_epochs_covering(@l, @mn, @mx)", ("l", lane), ("mn", min), ("mx", max));
    }
    await _executeAsync(conn, "SELECT _wh_bump_origin_generation()");
    await tx.CommitAsync();
  }

  private static async Task _seedAsync(NpgsqlConnection conn, Guid streamId, long commitSeq, long version,
      Guid? origin = null, long? originSeq = null) {
    var eventId = TrackedGuid.New().Value;
    await _executeAsync(conn, """
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
         commit_sequence, origin_service_id, origin_commit_sequence, flags, created_at)
      VALUES (@event, @stream, @stream, 'TestAggregate', 'Contracts.RewriteFloorProbe', 'null'::jsonb, @version,
              @seq, @origin, @originSeq, 0, NOW() - INTERVAL '2 hours')
      """,
      ("event", eventId), ("stream", streamId), ("version", version), ("seq", commitSeq),
      ("origin", (object?)origin ?? DBNull.Value), ("originSeq", (object?)originSeq ?? DBNull.Value));
    await _executeAsync(conn, """
      INSERT INTO wh_event_body (event_id, event_data, metadata)
      VALUES (@event, '{"seeded":true}'::jsonb, '{}'::jsonb)
      """, ("event", eventId));
    await EmitChainDigestFold.FoldAsync(conn, eventId);
  }

  private static async Task<string> _truncateAsync(NpgsqlConnection conn, Guid stream, long throughVersion) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT truncate_status FROM wh_truncate_stream_prefix(@s, @v, false)";
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("v", throughVersion);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  // ── the origin: which rewrites move the generation, and what they record ─────────────────────

  [Test]
  public async Task Bump_WithNoRewriteNoted_LeavesTheGenerationAloneAsync() {
    await using var conn = await _openAsync();
    var before = await _generationAsync(conn);

    await _executeAsync(conn, "SELECT _wh_bump_origin_generation()");

    await Assert.That(await _generationAsync(conn)).IsEqualTo(before)
      .Because("a bump with nothing rewritten in this origin's lane changed nothing a consumer verified");
  }

  [Test]
  public async Task Bump_AfterARewriteOfAnotherOriginsLaneOnly_LeavesTheGenerationAloneAsync() {
    await using var conn = await _openAsync();
    var before = await _generationAsync(conn);

    await _rewriteAsync(conn, (TrackedGuid.New().Value, 1, 9));

    await Assert.That(await _generationAsync(conn)).IsEqualTo(before)
      .Because("a received lane is no part of what this origin's manifests answer, so rewriting it invalidates no seal of it");
    await Assert.That(await _rewriteRowsAsync(conn)).IsEqualTo(0L);
  }

  [Test]
  public async Task Bump_AfterLocalRewrites_RecordsTheLowestPositionTouchedAsync() {
    await using var conn = await _openAsync();
    var before = await _generationAsync(conn);

    await _rewriteAsync(conn, (_localLane, 40, 50), (_localLane, 30, 35), (TrackedGuid.New().Value, 1, 2));

    await Assert.That(await _generationAsync(conn)).IsEqualTo(before + 1)
      .Because("one rewrite moves the generation once, however many ranges it refolded");
    await Assert.That(await _recordedFloorAsync(conn, before + 1)).IsEqualTo(30L)
      .Because("the floor is the lowest local position any of the rewrite's ranges touched; the received lane adds nothing");
  }

  [Test]
  public async Task Bump_ConsumesTheNote_SoASecondBumpInTheSameTransactionDoesNothingAsync() {
    await using var conn = await _openAsync();
    var before = await _generationAsync(conn);

    await using (var tx = await conn.BeginTransactionAsync()) {
      await _executeAsync(conn, "SELECT _wh_refold_epochs_covering(@l, 7, 9)", ("l", _localLane));
      await _executeAsync(conn, "SELECT _wh_bump_origin_generation()");
      await _executeAsync(conn, "SELECT _wh_bump_origin_generation()");
      await tx.CommitAsync();
    }

    await Assert.That(await _generationAsync(conn)).IsEqualTo(before + 1)
      .Because("the second bump follows no new rewrite");
  }

  [Test]
  public async Task Bump_ANoteFromARolledBackTransaction_DoesNotCarryOverAsync() {
    await using var conn = await _openAsync();
    var before = await _generationAsync(conn);

    await using (var tx = await conn.BeginTransactionAsync()) {
      await _executeAsync(conn, "SELECT _wh_refold_epochs_covering(@l, 7, 9)", ("l", _localLane));
      await tx.RollbackAsync();
    }
    await _executeAsync(conn, "SELECT _wh_bump_origin_generation()");

    await Assert.That(await _generationAsync(conn)).IsEqualTo(before)
      .Because("a rewrite that rolled back rewrote nothing; its note is transaction-local");
  }

  [Test]
  public async Task Refold_OfASealedLocalEpoch_NotesTheEpochsStartAsync() {
    await using var conn = await _openAsync();
    await _setSettingAsync(conn, "integrity_epoch_width", 10);
    var stream = TrackedGuid.New().Value;
    await _seedAsync(conn, stream, commitSeq: 5, version: 1);
    await _seedAsync(conn, stream, commitSeq: 15, version: 2);
    await _seedAsync(conn, stream, commitSeq: 35, version: 3);
    await _executeAsync(conn, "SELECT close_digest_epochs(NOW(), 3600, 100)");
    var before = await _generationAsync(conn);

    // The redelivery paths refold sealed local epochs directly, by epoch, not through the covering helper.
    await using (var tx = await conn.BeginTransactionAsync()) {
      await _executeAsync(conn, "SELECT refold_digest_epochs(@l, 1, 1)", ("l", _localLane));
      await _executeAsync(conn, "SELECT _wh_bump_origin_generation()");
      await tx.CommitAsync();
    }

    await Assert.That(await _generationAsync(conn)).IsEqualTo(before + 1);
    await Assert.That(await _recordedFloorAsync(conn, before + 1)).IsEqualTo(10L)
      .Because("a refolded epoch can have changed anywhere in it, so the floor is its first position");
  }

  [Test]
  public async Task ClosingANewEpoch_IsNoRewrite_AndNotesNothingAsync() {
    await using var conn = await _openAsync();
    await _setSettingAsync(conn, "integrity_epoch_width", 10);
    var stream = TrackedGuid.New().Value;
    await _seedAsync(conn, stream, commitSeq: 5, version: 1);
    await _seedAsync(conn, stream, commitSeq: 25, version: 2);
    var before = await _generationAsync(conn);

    await using (var tx = await conn.BeginTransactionAsync()) {
      await _executeAsync(conn, "SELECT close_digest_epochs(NOW(), 3600, 100)");
      await _executeAsync(conn, "SELECT _wh_bump_origin_generation()");
      await tx.CommitAsync();
    }

    await Assert.That(await _generationAsync(conn)).IsEqualTo(before)
      .Because("sealing a new epoch folds what was already there; nothing a consumer verified changed");
  }

  [Test]
  public async Task Truncate_OfAReceivedStream_LeavesTheGenerationAlone_AndOfALocalStream_RecordsItsFloorAsync() {
    await using var conn = await _openAsync();
    var received = TrackedGuid.New().Value;
    var otherOrigin = TrackedGuid.New().Value;
    await _seedAsync(conn, received, commitSeq: 101, version: 1, origin: otherOrigin, originSeq: 7);
    await _seedAsync(conn, received, commitSeq: 102, version: 2, origin: otherOrigin, originSeq: 8);
    var local = TrackedGuid.New().Value;
    await _seedAsync(conn, local, commitSeq: 60, version: 1);
    await _seedAsync(conn, local, commitSeq: 70, version: 2);
    await _seedAsync(conn, local, commitSeq: 80, version: 3);
    var before = await _generationAsync(conn);

    await Assert.That(await _truncateAsync(conn, received, 1)).IsEqualTo("truncated");
    await Assert.That(await _generationAsync(conn)).IsEqualTo(before)
      .Because("the superseding reaper, the truncate and the purge run on received lanes all day; none of it is this origin's history");

    await Assert.That(await _truncateAsync(conn, local, 2)).IsEqualTo("truncated");
    await Assert.That(await _generationAsync(conn)).IsEqualTo(before + 1);
    await Assert.That(await _recordedFloorAsync(conn, before + 1)).IsEqualTo(60L)
      .Because("the truncate removed local positions 60 and 70; nothing below 60 changed");
  }

  [Test]
  public async Task TheLog_KeepsTheConfiguredNumberOfRewrites_AndTheOldestGoAsync() {
    await using var conn = await _openAsync();
    await _setSettingAsync(conn, "integrity_origin_rewrite_log_rows", 3);
    var before = await _generationAsync(conn);

    for (var i = 1; i <= 5; i++) {
      await _rewriteAsync(conn, (_localLane, i * 100, i * 100 + 5));
    }

    await Assert.That(await _rewriteRowsAsync(conn)).IsEqualTo(3L);
    await Assert.That(await _recordedFloorAsync(conn, before + 2)).IsNull()
      .Because("the log is bounded: a consumer that fell further behind than it reaches is answered as unknown");
    await Assert.That(await _recordedFloorAsync(conn, before + 3)).IsEqualTo(300L);
  }

  // ── the origin's answer: what it can vouch for since a consumer's generation ────────────────

  [Test]
  public async Task HistoryRewrite_SinceAGenerationTheLogReaches_IsTheLowestFloorAfterItAsync() {
    await using var ctx = CreateDbContext();
    await using var conn = await _openAsync();
    var start = await _generationAsync(conn);
    await _rewriteAsync(conn, (_localLane, 500, 510));
    await _rewriteAsync(conn, (_localLane, 200, 210));
    await _rewriteAsync(conn, (_localLane, 900, 910));
    var coordinator = _coordinator(ctx);

    await Assert.That(await coordinator.GetIntegrityHistoryRewriteAsync(start))
      .IsEqualTo(new IntegrityHistoryRewrite(start, 200L))
      .Because("a consumer that last saw the start generation missed all three rewrites; the lowest of them bounds what it must re-verify");
    await Assert.That(await coordinator.GetIntegrityHistoryRewriteAsync(start + 2))
      .IsEqualTo(new IntegrityHistoryRewrite(start + 2, 900L))
      .Because("one that already absorbed the first two missed only the third");
    await Assert.That(await coordinator.GetIntegrityHistoryRewriteAsync(start + 3))
      .IsEqualTo(new IntegrityHistoryRewrite(start + 3, null))
      .Because("nothing was rewritten after the current generation");
  }

  [Test]
  public async Task HistoryRewrite_SinceAGenerationOlderThanTheLog_CannotBeVouchedForAsync() {
    await using var ctx = CreateDbContext();
    await using var conn = await _openAsync();
    var start = await _generationAsync(conn);
    await _setSettingAsync(conn, "integrity_origin_rewrite_log_rows", 2);
    for (var i = 1; i <= 4; i++) {
      await _rewriteAsync(conn, (_localLane, i * 100, i * 100 + 5));
    }
    var coordinator = _coordinator(ctx);

    await Assert.That(await coordinator.GetIntegrityHistoryRewriteAsync(start + 1)).IsNull()
      .Because("the rewrites after it are no longer all in the log, so the origin cannot say how far they reached");
    await Assert.That(await coordinator.GetIntegrityHistoryRewriteAsync(start + 2))
      .IsEqualTo(new IntegrityHistoryRewrite(start + 2, 300L));
  }

  [Test]
  public async Task HistoryRewrite_SinceAGenerationFromBeforeTheLogExisted_CannotBeVouchedForAsync() {
    await using var ctx = CreateDbContext();
    await using var conn = await _openAsync();
    var start = await _generationAsync(conn);
    // As if 30 bumps had happened before the log existed: the log reaches back to the generation it started at.
    await _setSettingAsync(conn, "integrity_origin_generation", start + 30);
    await _setSettingAsync(conn, "integrity_origin_rewrite_log_from", start + 30);
    var coordinator = _coordinator(ctx);

    await Assert.That(await coordinator.GetIntegrityHistoryRewriteAsync(start + 29)).IsNull()
      .Because("a bump from before the log recorded no floor");
    await Assert.That(await coordinator.GetIntegrityHistoryRewriteAsync(start + 30))
      .IsEqualTo(new IntegrityHistoryRewrite(start + 30, null));
  }

  // ── the consumer: what a new generation does to its seal ─────────────────────────────────────

  [Test]
  public async Task Guard_ARewriteAtOrAboveTheSeal_KeepsTheSeal_AndProceedsAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = TrackedGuid.New().Value;
    await coordinator.EnsureIntegritySealGenerationAsync(origin, 5);
    await coordinator.AdvanceIntegritySealAsync(origin, 400);

    await Assert.That(await coordinator.EnsureIntegritySealGenerationAsync(origin, 6, new IntegrityHistoryRewrite(5, 400)))
      .IsTrue().Because("nothing below the seal changed, so this round's comparison is as good as any");
    await Assert.That(await coordinator.GetIntegritySealAsync(origin)).IsEqualTo(400L)
      .Because("verified history the rewrite did not reach stays verified");
    await Assert.That(await coordinator.GetIntegritySealGenerationAsync(origin)).IsEqualTo(6L);
  }

  [Test]
  public async Task Guard_ARewriteBelowTheSeal_LowersItToTheRewritePoint_NotToZeroAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = TrackedGuid.New().Value;
    await coordinator.EnsureIntegritySealGenerationAsync(origin, 5);
    await coordinator.AdvanceIntegritySealAsync(origin, 400);

    await Assert.That(await coordinator.EnsureIntegritySealGenerationAsync(origin, 8, new IntegrityHistoryRewrite(5, 250)))
      .IsFalse().Because("the round in flight was asked from the old seal; sealing it would certify the rewritten range unverified");
    await Assert.That(await coordinator.GetIntegritySealAsync(origin)).IsEqualTo(250L)
      .Because("only the history from the rewrite point up has to be verified again");
    await Assert.That(await coordinator.EnsureIntegritySealGenerationAsync(origin, 8, new IntegrityHistoryRewrite(5, 250)))
      .IsTrue().Because("each chunk of the same answer carries the same generation; the seal falls once");
    await Assert.That(await coordinator.GetIntegritySealAsync(origin)).IsEqualTo(250L);
  }

  [Test]
  public async Task Guard_ARewriteNamingNoPosition_KeepsTheSealAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = TrackedGuid.New().Value;
    await coordinator.EnsureIntegritySealGenerationAsync(origin, 5);
    await coordinator.AdvanceIntegritySealAsync(origin, 400);

    await Assert.That(await coordinator.EnsureIntegritySealGenerationAsync(origin, 6, new IntegrityHistoryRewrite(5, null)))
      .IsTrue();
    await Assert.That(await coordinator.GetIntegritySealAsync(origin)).IsEqualTo(400L);
  }

  [Test]
  public async Task Guard_FrequentBumpsAboveTheSeal_NeverResetItAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = TrackedGuid.New().Value;
    await coordinator.EnsureIntegritySealGenerationAsync(origin, 0);
    await coordinator.AdvanceIntegritySealAsync(origin, 1_000);

    for (var generation = 1L; generation <= 50; generation++) {
      await Assert.That(await coordinator.EnsureIntegritySealGenerationAsync(
        origin, generation, new IntegrityHistoryRewrite(generation - 1, 1_000 + generation))).IsTrue();
      await coordinator.AdvanceIntegritySealAsync(origin, 1_000 + generation);
    }

    await Assert.That(await coordinator.GetIntegritySealAsync(origin)).IsEqualTo(1_050L)
      .Because("routine rewrites at the head never discard the verified history behind them");
  }

  [Test]
  public async Task Guard_AFloorFromAGenerationNewerThanTheRecordedOne_IsUnknown_AndResetsAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = TrackedGuid.New().Value;
    await coordinator.EnsureIntegritySealGenerationAsync(origin, 5);
    await coordinator.AdvanceIntegritySealAsync(origin, 400);

    await Assert.That(await coordinator.EnsureIntegritySealGenerationAsync(origin, 9, new IntegrityHistoryRewrite(7, 350)))
      .IsFalse();
    await Assert.That(await coordinator.GetIntegritySealAsync(origin)).IsEqualTo(0L)
      .Because("the floor says nothing about the rewrites between the recorded generation and the one it starts from");
  }

  [Test]
  public async Task Guard_AnOlderGeneration_ResetsAsBeforeAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = TrackedGuid.New().Value;
    await coordinator.EnsureIntegritySealGenerationAsync(origin, 9);
    await coordinator.AdvanceIntegritySealAsync(origin, 400);

    await Assert.That(await coordinator.EnsureIntegritySealGenerationAsync(origin, 4, new IntegrityHistoryRewrite(4, null)))
      .IsFalse();
    await Assert.That(await coordinator.GetIntegritySealAsync(origin)).IsEqualTo(0L)
      .Because("a generation that went back means the origin's history is not the one the seal verified");
    await Assert.That(await coordinator.GetIntegritySealGenerationAsync(origin)).IsEqualTo(4L);
  }

  [Test]
  public async Task Guard_WithoutAFloor_ResetsAsBeforeAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = TrackedGuid.New().Value;
    await coordinator.EnsureIntegritySealGenerationAsync(origin, 5);
    await coordinator.AdvanceIntegritySealAsync(origin, 400);

    await Assert.That(await coordinator.EnsureIntegritySealGenerationAsync(origin, 6, rewrite: null)).IsFalse();
    await Assert.That(await coordinator.GetIntegritySealAsync(origin)).IsEqualTo(0L)
      .Because("an origin that cannot say what changed (an older one, or a consumer past its log) is answered by the old reset");
  }

  [Test]
  public async Task SealGeneration_IsNullBeforeFirstContactAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    await Assert.That(await coordinator.GetIntegritySealGenerationAsync(TrackedGuid.New().Value)).IsNull();
  }
}
