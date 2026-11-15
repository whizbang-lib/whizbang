// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2353: the sealed-history re-check resumes where it stopped and reaches back by time. Every sealed epoch records when
/// it was sealed and when it was last re-checked (<c>wh_digest_epoch_seals</c>); a re-check takes the epochs sealed within
/// its lookback that were not re-checked within its interval, least recently re-checked first, up to its cap. Before
/// this, <c>verify_digest_epochs</c> walked from epoch 0 on every call, so the epochs past its cap were never re-checked.
/// </summary>
/// <docs>resilience/stream-integrity#seal-re-check</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/450_SealedEpochsRecordWhenTheyWereSealed.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class SealRecheckRotationSqlTests : EFCoreTestBase {
  private const string TYPE = "Contracts.SealRecheckProbe";

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static async Task _executeAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>Seeds one settled local-lane event at a commit sequence, folded into its digest bucket.</summary>
  private static async Task _seedAsync(NpgsqlConnection conn, long commitSeq, bool settled = true) {
    var eventId = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    await using (var store = conn.CreateCommand()) {
      store.CommandText = $"""
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, flags, created_at)
        VALUES (@e, @s, @s, 'TestAggregate', @t, 'null'::jsonb, 1, @seq, 0, NOW() - INTERVAL '{(settled ? "2 hours" : "0 seconds")}')
        """;
      store.Parameters.AddWithValue("e", eventId);
      store.Parameters.AddWithValue("s", stream);
      store.Parameters.AddWithValue("t", TYPE);
      store.Parameters.AddWithValue("seq", commitSeq);
      await store.ExecuteNonQueryAsync();
    }
    await using var body = conn.CreateCommand();
    body.CommandText = "INSERT INTO wh_event_body (event_id, event_data, metadata) VALUES (@e, '{}'::jsonb, '{}'::jsonb)";
    body.Parameters.AddWithValue("e", eventId);
    await body.ExecuteNonQueryAsync();
    await EmitChainDigestFold.FoldAsync(conn, eventId);
  }

  /// <summary>Width 100, one event in each of epochs 0 to 3 and one in epoch 4 (open), then closes 0 to 3.</summary>
  private static async Task _sealFourEpochsAsync(NpgsqlConnection conn) {
    await _executeAsync(conn, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
      VALUES ('integrity_epoch_width', '100', 'integer', 'test')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """);
    foreach (var seq in new long[] { 5, 105, 205, 305, 450 }) {
      await _seedAsync(conn, seq);
    }
    await _executeAsync(conn, "SELECT close_digest_epochs(NOW(), 3600, 100)");
  }

  private static async Task<(int Checked, int Drifted)> _recheckAsync(NpgsqlConnection conn,
      int max, string now = "NOW()", long? lookbackSeconds = null, long everySeconds = 0, bool refold = true) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText =
      $"SELECT epochs_checked, epochs_drifted FROM wh_recheck_digest_epochs({now}, 3600, @max, @lookback, @every, @refold)";
    cmd.Parameters.AddWithValue(nameof(max), max);
    cmd.Parameters.Add(new NpgsqlParameter("lookback", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)lookbackSeconds ?? DBNull.Value });
    cmd.Parameters.AddWithValue("every", everySeconds);
    cmd.Parameters.AddWithValue(nameof(refold), refold);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt32(0), reader.GetInt32(1));
  }

  private static async Task<List<long>> _recheckedEpochsAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT epoch_id FROM wh_digest_epoch_seals WHERE rechecked_at IS NOT NULL ORDER BY epoch_id";
    var epochs = new List<long>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      epochs.Add(reader.GetInt64(0));
    }
    return epochs;
  }

  private static Task _corruptAsync(NpgsqlConnection conn, long epoch) =>
    _executeAsync(conn, $"UPDATE wh_digest_epochs SET digest_lo = 666 WHERE event_type = '{TYPE}' AND epoch_id = {epoch}");

  private static Task<long> _storedLoAsync(NpgsqlConnection conn, long epoch) =>
    _scalarAsync(conn, $"SELECT digest_lo FROM wh_digest_epochs WHERE event_type = '{TYPE}' AND epoch_id = {epoch}");

  // ── the record ─────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Closing_RecordsWhenEachEpochWasSealed_EmptyEpochsIncludedAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
      VALUES ('integrity_epoch_width', '100', 'integer', 'test')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """);
    // Epoch 1 holds nothing: it is sealed all the same, and a re-check must still be able to find it.
    await _seedAsync(conn, 5);
    await _seedAsync(conn, 205);
    await _seedAsync(conn, 350);

    await _executeAsync(conn, "SELECT close_digest_epochs(TIMESTAMPTZ '2030-01-02 03:04:05+00', 3600, 100)");

    await Assert.That(await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_digest_epoch_seals")).IsEqualTo(3L)
      .Because("epochs 0, 1 and 2 were sealed; the empty epoch 1 has no bucket row but is sealed");
    await Assert.That(await _scalarAsync(conn,
        "SELECT COUNT(*) FROM wh_digest_epoch_seals WHERE sealed_at = TIMESTAMPTZ '2030-01-02 03:04:05+00' AND rechecked_at IS NULL"))
      .IsEqualTo(3L).Because("each records the closure's time and no re-check yet");
  }

  [Test]
  public async Task AFrontierThatGoesBack_ForgetsTheEpochsAboveItAsync() {
    await using var conn = await _openAsync();
    await _sealFourEpochsAsync(conn);

    await _executeAsync(conn, "UPDATE wh_digest_epoch_frontiers SET closed_through_epoch = 1");

    await Assert.That(await _scalarAsync(conn, "SELECT MAX(epoch_id) FROM wh_digest_epoch_seals")).IsEqualTo(1L)
      .Because("an epoch above the frontier is no longer sealed, so it has nothing to re-check");
  }

  [Test]
  public async Task TheBackfill_RecordsEpochsSealedBeforeTheRecordExisted_AtTheirFoldTimeAsync() {
    await using var conn = await _openAsync();
    await _sealFourEpochsAsync(conn);
    await _executeAsync(conn, "UPDATE wh_digest_epochs SET closed_at = TIMESTAMPTZ '2029-05-05 00:00:00+00' WHERE epoch_id = 2");
    await _executeAsync(conn, "DELETE FROM wh_digest_epoch_seals");

    var recorded = await _scalarAsync(conn, "SELECT _wh_backfill_epoch_seals()");

    await Assert.That(recorded).IsEqualTo(4L).Because("every epoch at or below the frontier is recorded once");
    await Assert.That(await _scalarAsync(conn,
        "SELECT COUNT(*) FROM wh_digest_epoch_seals WHERE epoch_id = 2 AND sealed_at = TIMESTAMPTZ '2029-05-05 00:00:00+00'"))
      .IsEqualTo(1L).Because("an epoch with buckets is sealed when its fold was written");
    await Assert.That(await _scalarAsync(conn, "SELECT _wh_backfill_epoch_seals()")).IsEqualTo(0L)
      .Because("the backfill is idempotent");
  }

  // ── the rotation ───────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Recheck_ResumesPastWhatItChecked_NeverRestartingAtTheFirstEpochAsync() {
    await using var conn = await _openAsync();
    await _sealFourEpochsAsync(conn);

    var first = await _recheckAsync(conn, max: 2);
    var afterFirst = await _recheckedEpochsAsync(conn);
    var second = await _recheckAsync(conn, max: 2, now: "NOW() + INTERVAL '1 second'");

    await Assert.That(first.Checked).IsEqualTo(2);
    await Assert.That(afterFirst).IsEquivalentTo(new List<long> { 0, 1 });
    await Assert.That(second.Checked).IsEqualTo(2);
    await Assert.That(await _recheckedEpochsAsync(conn)).IsEquivalentTo(new List<long> { 0, 1, 2, 3 })
      .Because("the second run takes the epochs the first did not reach, instead of epochs 0 and 1 again");
  }

  [Test]
  public async Task VerifyDigestEpochs_RotatesThroughTheEpochs_InsteadOfRestartingAtZeroAsync() {
    await using var conn = await _openAsync();
    await _sealFourEpochsAsync(conn);
    await _corruptAsync(conn, 3);

    var drifted = 0L;
    for (var run = 0; run < 4; run++) {
      drifted += await _scalarAsync(conn, "SELECT epochs_drifted FROM verify_digest_epochs(NOW(), 3600, 1)");
    }

    await Assert.That(drifted).IsEqualTo(1L)
      .Because("four runs capped at one epoch each reach the fourth epoch; restarting at zero never would");
    await Assert.That(await _storedLoAsync(conn, 3)).IsNotEqualTo(666L).Because("and the old entry still refolds");
  }

  [Test]
  public async Task Recheck_LeavesEpochsSealedBeforeTheLookbackAloneAsync() {
    await using var conn = await _openAsync();
    await _sealFourEpochsAsync(conn);
    await _executeAsync(conn, "UPDATE wh_digest_epoch_seals SET sealed_at = NOW() - INTERVAL '40 days' WHERE epoch_id IN (0, 1)");

    var result = await _recheckAsync(conn, max: 100, lookbackSeconds: (long)TimeSpan.FromDays(30).TotalSeconds);

    await Assert.That(result.Checked).IsEqualTo(2);
    await Assert.That(await _recheckedEpochsAsync(conn)).IsEquivalentTo(new List<long> { 2, 3 })
      .Because("an epoch sealed before the lookback is trusted as sealed");
  }

  [Test]
  public async Task Recheck_ChecksAnEpochAtMostOncePerInterval_ThenAgainAsync() {
    await using var conn = await _openAsync();
    await _sealFourEpochsAsync(conn);
    var week = (long)TimeSpan.FromDays(7).TotalSeconds;

    var first = await _recheckAsync(conn, max: 100, everySeconds: week);
    var sameDay = await _recheckAsync(conn, max: 100, now: "NOW() + INTERVAL '1 day'", everySeconds: week);
    var nextWeek = await _recheckAsync(conn, max: 100, now: "NOW() + INTERVAL '8 days'", everySeconds: week);

    await Assert.That(first.Checked).IsEqualTo(4);
    await Assert.That(sameDay.Checked).IsEqualTo(0).Because("every epoch was re-checked within the interval");
    await Assert.That(nextWeek.Checked).IsEqualTo(4).Because("once the interval passes, each is due again");
  }

  [Test]
  public async Task Recheck_ADriftedEpoch_IsRefoldedOrOnlyReported_AsAskedAsync() {
    await using var conn = await _openAsync();
    await _sealFourEpochsAsync(conn);
    await _corruptAsync(conn, 1);

    var reported = await _recheckAsync(conn, max: 100, refold: false);

    await Assert.That(reported.Drifted).IsEqualTo(1);
    await Assert.That(await _storedLoAsync(conn, 1)).IsEqualTo(666L).Because("report-only leaves the seal for an operator");

    var refolded = await _recheckAsync(conn, max: 100, now: "NOW() + INTERVAL '1 second'");

    await Assert.That(refolded.Drifted).IsEqualTo(1);
    await Assert.That(await _storedLoAsync(conn, 1)).IsNotEqualTo(666L).Because("refold rewrites the seal from the store");
    await Assert.That((await _recheckAsync(conn, max: 100, now: "NOW() + INTERVAL '2 seconds'")).Drifted).IsEqualTo(0);
  }

  [Test]
  public async Task Recheck_AnEpochWithAnUnsettledArrival_IsSkippedAndStaysFirstAsync() {
    await using var conn = await _openAsync();
    await _sealFourEpochsAsync(conn);
    await _seedAsync(conn, 42, settled: false);

    var result = await _recheckAsync(conn, max: 1);

    await Assert.That(result.Checked).IsEqualTo(1);
    await Assert.That(await _recheckedEpochsAsync(conn)).IsEquivalentTo(new List<long> { 1 })
      .Because("epoch 0 holds an in-flight arrival, so it is skipped, not spent, and the cap goes to the next epoch");
  }

  [Test]
  public async Task Coordinator_RecheckDigestEpochsAsync_DrivesTheSqlFunctionAsync() {
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    await _sealFourEpochsAsync(conn);
    await _corruptAsync(conn, 0);
    await _corruptAsync(conn, 2);

    var result = await _coordinator(ctx).RecheckDigestEpochsAsync(new SealRecheckRequest(
      SettleWindow: TimeSpan.FromHours(1), MaxEpochs: 3, Lookback: TimeSpan.FromDays(30),
      RecheckEvery: TimeSpan.FromDays(7), Refold: false));

    await Assert.That(result.EpochsChecked).IsEqualTo(3);
    await Assert.That(result.EpochsDrifted).IsEqualTo(2);
    await Assert.That(await _storedLoAsync(conn, 0)).IsEqualTo(666L).Because("Refold false reports only");
  }
}
