// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Stream-integrity Phase B: <c>AdvanceIntegrityCheckpointAsync</c> baselines on first run (no
/// retroactive history counting), then advances the watermark to the highest stamped commit
/// sequence and returns per-(tenant, type) counts inside the advanced window — excluding
/// at-most-once occurrences and audit records. A quiet window advances to an empty (but non-null) window, keeping
/// the liveness beat alive.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityCheckpoint.cs</code-under-test>
[Category("Integration")]
[NotInParallel("IntegrityCheckpoint")]
[Category("Shard3")]
public class IntegrityCheckpointAdvanceTests : EFCoreTestBase {

  private const string TENANT_A = "tenant-a";

  /// <summary>The event-store flag an at-most-once occurrence carries (<c>EventFlags.AtMostOnce</c>).</summary>
  private const int AT_MOST_ONCE_FLAG = 32;

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task Advance_BaselinesThenCountsThenIdlesAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _resetWatermarkAsync(conn);
    var coordinator = _coordinator(ctx);

    // History BEFORE the first advance must never be counted retroactively.
    await _seedAsync(conn, TrackedGuid.New().Value, 1, TENANT_A, "Contracts.OldHistory");

    var baseline = await coordinator.AdvanceIntegrityCheckpointAsync();
    await Assert.That(baseline).IsNotNull();
    await Assert.That(baseline!.FromCommitSequence).IsEqualTo(baseline.ToCommitSequence)
      .Because("the first run baselines at the current head — an empty window, no history storm.");
    await Assert.That(baseline.Buckets).IsEmpty();

    // A live window: 2× typeX + 1× typeY in tenant A, plus an at-most-once occurrence (excluded).
    var stream = TrackedGuid.New().Value;
    await _seedAsync(conn, stream, 2, TENANT_A, "Contracts.TypeX");
    await _seedAsync(conn, stream, 3, TENANT_A, "Contracts.TypeX");
    await _seedAsync(conn, stream, 4, TENANT_A, "Contracts.TypeY");
    await _seedAsync(conn, stream, 5, TENANT_A, "Contracts.OccurrenceFired",
      flags: AT_MOST_ONCE_FLAG);

    var window = await coordinator.AdvanceIntegrityCheckpointAsync();
    await Assert.That(window).IsNotNull();
    await Assert.That(window!.FromCommitSequence).IsEqualTo(baseline.ToCommitSequence)
      .Because("windows chain: this window's exclusive floor is the previous watermark.");
    await Assert.That(window.ToCommitSequence - window.FromCommitSequence).IsEqualTo(4L)
      .Because("four events were stamped inside the window (the excluded occurrence still advances the watermark).");
    var buckets = window.Buckets.ToDictionary(b => b.EventType, b => b);
    await Assert.That(buckets["Contracts.TypeX"].Count).IsEqualTo(2);
    await Assert.That(buckets["Contracts.TypeX"].TenantScope).IsEqualTo(TENANT_A);
    await Assert.That(buckets["Contracts.TypeY"].Count).IsEqualTo(1);
    await Assert.That(buckets.ContainsKey("Contracts.OccurrenceFired")).IsFalse()
      .Because("an undelivered at-most-once occurrence is its declared behavior, not a gap — " +
               "counting it would alarm consumers about legitimate non-delivery.");

    // A quiet window still advances (same-value CAS) and returns EMPTY, not null — the worker
    // publishes it as the liveness beat.
    var quiet = await coordinator.AdvanceIntegrityCheckpointAsync();
    await Assert.That(quiet).IsNotNull();
    await Assert.That(quiet!.FromCommitSequence).IsEqualTo(quiet.ToCommitSequence);
    await Assert.That(quiet.Buckets).IsEmpty();
  }

  [Test]
  public async Task Advance_ExcludesStoredCheckpointEvents_FromTheirOwnBucketsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _resetWatermarkAsync(conn);
    var coordinator = _coordinator(ctx);

    await coordinator.AdvanceIntegrityCheckpointAsync();   // baseline

    // A stored checkpoint event lands in wh_event_store under the WIRE form ("Type, Assembly") —
    // the exclusion must match that form, or checkpoints count themselves and every window
    // over-declares by its own heartbeat.
    var stream = TrackedGuid.New().Value;
    await _seedAsync(conn, stream, 1, TENANT_A,
      Whizbang.Core.TypeNameFormatter.Format(typeof(Whizbang.Core.Messaging.IntegrityCheckpoint)));
    await _seedAsync(conn, stream, 2, TENANT_A, "Contracts.TypeX");

    var window = await coordinator.AdvanceIntegrityCheckpointAsync();
    await Assert.That(window!.Buckets.Select(b => b.EventType).ToList())
      .IsEquivalentTo(["Contracts.TypeX"])
      .Because("checkpoints never count themselves — only the domain event may bucket.");
  }

  [Test]
  public async Task CountReceivedFromOrigin_CountsWindowedByOriginIdentityAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var coordinator = _coordinator(ctx);

    var origin = TrackedGuid.New().Value;
    var otherOrigin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    // Received events persist the ORIGIN identity; locally-originated rows have neither column.
    await _seedReceivedAsync(conn, stream, 1, TENANT_A, "Contracts.TypeX", origin, originSeq: 11);
    await _seedReceivedAsync(conn, stream, 2, TENANT_A, "Contracts.TypeX", origin, originSeq: 12);
    await _seedReceivedAsync(conn, stream, 3, TENANT_A, "Contracts.TypeY", origin, originSeq: 13);
    await _seedReceivedAsync(conn, stream, 4, TENANT_A, "Contracts.TypeX", origin, originSeq: 25);   // outside window
    await _seedReceivedAsync(conn, stream, 5, TENANT_A, "Contracts.TypeX", otherOrigin, originSeq: 12);   // other origin
    await _seedAsync(conn, stream, 6, TENANT_A, "Contracts.TypeX");   // local — no origin stamp

    var counts = await coordinator.CountReceivedFromOriginAsync(origin, fromCommitSequence: 10, toCommitSequence: 20);

    var byType = counts.ToDictionary(b => b.EventType, b => b);
    await Assert.That(byType["Contracts.TypeX"].Count).IsEqualTo(2)
      .Because("only THIS origin's events inside the ORIGIN's commit-sequence window count — " +
               "other origins, out-of-window rows, and locally-originated rows are all excluded.");
    await Assert.That(byType["Contracts.TypeX"].TenantScope).IsEqualTo(TENANT_A);
    await Assert.That(byType["Contracts.TypeY"].Count).IsEqualTo(1);
    await Assert.That(counts.Count).IsEqualTo(2);
  }

  [Test]
  public async Task CountReceivedFromOrigin_UnscopedEvent_BucketsWithNoTenantAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var coordinator = _coordinator(ctx);

    var origin = TrackedGuid.New().Value;
    var stream = TrackedGuid.New().Value;
    // A received event whose scope carries no tenant (a global, unscoped stream).
    await _seedReceivedAsync(conn, stream, 1, tenant: null, "Contracts.TypeX", origin, originSeq: 11);
    await _seedReceivedAsync(conn, stream, 2, TENANT_A, "Contracts.TypeX", origin, originSeq: 12);

    var counts = await coordinator.CountReceivedFromOriginAsync(origin, fromCommitSequence: 10, toCommitSequence: 20);

    await Assert.That(counts.Select(b => $"{b.TenantScope ?? "(none)"}|{b.EventType}={b.Count}").ToList())
      .IsEquivalentTo(["(none)|Contracts.TypeX=1", $"{TENANT_A}|Contracts.TypeX=1"])
      .Because("an event with no tenant in its scope buckets under a null tenant, the same unscoped key the " +
               "origin's half reports, never under an empty-string tenant that would read as a distinct bucket.");
  }

  [Test]
  public async Task CheckpointCounts_LeaveAuditRecordsOut_OnBothSidesAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _resetWatermarkAsync(conn);
    var coordinator = _coordinator(ctx);
    string[] auditTypes = [
      Whizbang.Core.TypeNameFormatter.Format(typeof(Whizbang.Core.SystemEvents.EventAudited)),
      Whizbang.Core.TypeNameFormatter.Format(typeof(Whizbang.Core.SystemEvents.CommandAudited)),
    ];

    await coordinator.AdvanceIntegrityCheckpointAsync();   // baseline

    // The origin stores a domain event and, like any service that keeps audit records, the audit records of it.
    var stream = TrackedGuid.New().Value;
    await _seedAsync(conn, stream, 1, TENANT_A, "Contracts.TypeX");
    await _seedAsync(conn, stream, 2, TENANT_A, auditTypes[0]);
    await _seedAsync(conn, stream, 3, TENANT_A, auditTypes[1]);
    var window = await coordinator.AdvanceIntegrityCheckpointAsync();

    // A subscriber stores what it receives under the origin's identity, audit records included.
    var origin = TrackedGuid.New().Value;
    var received = TrackedGuid.New().Value;
    await _seedReceivedAsync(conn, received, 1, TENANT_A, "Contracts.TypeX", origin, window!.FromCommitSequence + 1);
    await _seedReceivedAsync(conn, received, 2, TENANT_A, auditTypes[0], origin, window.FromCommitSequence + 2);
    await _seedReceivedAsync(conn, received, 3, TENANT_A, auditTypes[1], origin, window.FromCommitSequence + 3);
    var counts = await coordinator.CountReceivedFromOriginAsync(origin, window.FromCommitSequence, window.ToCommitSequence);

    await Assert.That(window.Buckets.Select(b => $"{b.EventType}={b.Count}").ToList())
      .IsEquivalentTo(["Contracts.TypeX=1"])
      .Because("the origin's checkpoint leaves audit records out, as every integrity digest does (#1492): " +
               "the origin publishes its audit records without necessarily storing them.");
    await Assert.That(counts.Select(b => $"{b.EventType}={b.Count}").ToList())
      .IsEquivalentTo(["Contracts.TypeX=1"])
      .Because("the subscriber's half applies the same exclusion, so audit traffic it received never reads as a " +
               "count the origin did not declare (#1554).");
  }

  [Test]
  public async Task Advance_HistoryStampedAfterTheBaseline_IsNotCountedAsALiveWindowAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _resetWatermarkAsync(conn);
    var coordinator = _coordinator(ctx);

    // Right after an upgrade the history is still unstamped: the stamper gives it sequences over the next
    // minutes or hours, all of them above the baseline the first advance takes.
    var history = TrackedGuid.New().Value;
    await _seedAsync(conn, history, 1, TENANT_A, "Contracts.OldHistory", stamped: false);
    await _seedAsync(conn, history, 2, TENANT_A, "Contracts.OldHistory", stamped: false);

    var baseline = await coordinator.AdvanceIntegrityCheckpointAsync();
    await Assert.That(baseline!.Buckets).IsEmpty();

    // The stamper catches up on the history, and live traffic arrives after it.
    await Assert.That(await _stampAsync(conn)).IsEqualTo(2);
    await _seedAsync(conn, TrackedGuid.New().Value, 1, TENANT_A, "Contracts.TypeX");

    var window = await coordinator.AdvanceIntegrityCheckpointAsync();

    await Assert.That(window!.ToCommitSequence - window.FromCommitSequence).IsEqualTo(3L)
      .Because("the watermark still advances over every sequence the stamper handed out.");
    await Assert.That(window.Buckets.Select(b => $"{b.EventType}={b.Count}").ToList())
      .IsEquivalentTo(["Contracts.TypeX=1"])
      .Because("history written before the baseline was delivered long ago under no sequence, so no consumer can " +
               "hold it under the one the stamper gave it later; counting it would make every bucket a gap (#1791). " +
               "History is verified by the digests, not by live windows.");
  }

  [Test]
  public async Task Advance_AStoreBaselinedBeforeTheHistoryFloorExisted_TakesTheFloorOnceAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _resetWatermarkAsync(conn);
    var coordinator = _coordinator(ctx);

    // A store an earlier version baselined: the watermark is there, the history floor is not, and the
    // history it baselined over is still being stamped.
    var history = TrackedGuid.New().Value;
    await _seedAsync(conn, history, 1, TENANT_A, "Contracts.OldHistory", stamped: false);
    await coordinator.AdvanceIntegrityCheckpointAsync();
    await _deleteSettingAsync(conn, HISTORY_FLOOR_KEY);
    await _stampAsync(conn);

    var upgraded = await coordinator.AdvanceIntegrityCheckpointAsync();
    await Assert.That(upgraded!.Buckets).IsEmpty()
      .Because("the first advance that finds no floor takes it at the head, so the history still being stamped " +
               "is left to the digests rather than counted as live.");
    await Assert.That(await _settingAsync(conn, HISTORY_FLOOR_KEY)).IsNotNull();

    await _seedAsync(conn, TrackedGuid.New().Value, 1, TENANT_A, "Contracts.TypeX");
    var live = await coordinator.AdvanceIntegrityCheckpointAsync();
    await Assert.That(live!.Buckets.Select(b => $"{b.EventType}={b.Count}").ToList())
      .IsEquivalentTo(["Contracts.TypeX=1"])
      .Because("events written after the floor was taken are live and counted as before.");
  }

  [Test]
  public async Task Advance_ANewBaseline_MovesTheHistoryFloorWithItAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _resetWatermarkAsync(conn);
    var coordinator = _coordinator(ctx);

    await coordinator.AdvanceIntegrityCheckpointAsync();
    var firstFloor = await _settingAsync(conn, HISTORY_FLOOR_KEY);

    // Everything written since is history to the next baseline (an operator reset the watermark).
    await _seedAsync(conn, TrackedGuid.New().Value, 1, TENANT_A, "Contracts.Between", stamped: false);
    await _deleteSettingAsync(conn, WATERMARK_KEY);
    await coordinator.AdvanceIntegrityCheckpointAsync();
    await _stampAsync(conn);

    await Assert.That(await _settingAsync(conn, HISTORY_FLOOR_KEY)).IsNotEqualTo(firstFloor)
      .Because("a baseline restarts counting, so the floor it leaves history under is taken again with it.");
    var window = await coordinator.AdvanceIntegrityCheckpointAsync();
    await Assert.That(window!.Buckets).IsEmpty();
  }

  // ── seeding ──────────────────────────────────────────────────────────────

  /// <summary>Seeds one RECEIVED event: origin identity stamped, as the 1:1 forward persist does.</summary>
  private static async Task _seedReceivedAsync(
      NpgsqlConnection conn, Guid streamId, int version, string? tenant, string eventType,
      Guid originServiceId, long originSeq) {
    var eventId = TrackedGuid.New().Value;
    await using var store = conn.CreateCommand();
    store.CommandText = @"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, flags, origin_service_id, origin_commit_sequence)
      VALUES (@event, @stream, @stream, 'TestAggregate', @type, @scope::jsonb, @version, nextval('wh_commit_seq'), 0, @origin, @oseq)";
    store.Parameters.AddWithValue("event", eventId);
    store.Parameters.AddWithValue("stream", streamId);
    store.Parameters.AddWithValue("type", eventType);
    store.Parameters.AddWithValue("scope", tenant is null ? "{}" : $"{{\"t\":\"{tenant}\"}}");
    store.Parameters.AddWithValue(nameof(version), version);
    store.Parameters.AddWithValue("origin", originServiceId);
    store.Parameters.AddWithValue("oseq", originSeq);
    await store.ExecuteNonQueryAsync();
  }

  private const string WATERMARK_KEY = "integrity_checkpoint_watermark";
  private const string HISTORY_FLOOR_KEY = "integrity_checkpoint_history_floor";

  private static async Task _resetWatermarkAsync(NpgsqlConnection conn) {
    await _deleteSettingAsync(conn, WATERMARK_KEY);
    await _deleteSettingAsync(conn, HISTORY_FLOOR_KEY);
  }

  private static async Task _deleteSettingAsync(NpgsqlConnection conn, string key) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM wh_settings WHERE setting_key = @key";
    cmd.Parameters.AddWithValue(nameof(key), key);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<string?> _settingAsync(NpgsqlConnection conn, string key) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT setting_value FROM wh_settings WHERE setting_key = @key";
    cmd.Parameters.AddWithValue(nameof(key), key);
    return (string?)await cmd.ExecuteScalarAsync();
  }

  /// <summary>Runs the production commit-order stamper over every unstamped row.</summary>
  private static async Task<int> _stampAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT stamp_pending_commit_sequences(1000)";
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  /// <summary>Seeds one stamped event (store pointer + body).</summary>
  private static async Task<Guid> _seedAsync(
      NpgsqlConnection conn, Guid streamId, int version, string tenant, string eventType,
      int flags = 0, bool stamped = true) {
    var eventId = TrackedGuid.New().Value;
    await using (var store = conn.CreateCommand()) {
      store.CommandText = @"
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, flags)
        VALUES (@event, @stream, @stream, 'TestAggregate', @type, @scope::jsonb, @version,
                CASE WHEN @stamped THEN nextval('wh_commit_seq') END, @flags)";
      store.Parameters.AddWithValue("event", eventId);
      store.Parameters.AddWithValue("stream", streamId);
      store.Parameters.AddWithValue("type", eventType);
      store.Parameters.AddWithValue("scope", $"{{\"t\":\"{tenant}\"}}");
      store.Parameters.AddWithValue(nameof(version), version);
      store.Parameters.AddWithValue(nameof(flags), flags);
      store.Parameters.AddWithValue(nameof(stamped), stamped);
      await store.ExecuteNonQueryAsync();
    }
    await using (var body = conn.CreateCommand()) {
      body.CommandText = """

        INSERT INTO wh_event_body (event_id, event_data, metadata)
        VALUES (@event, '{"seeded":true}'::jsonb, @meta::jsonb)
""";
      body.Parameters.AddWithValue("event", eventId);
      body.Parameters.AddWithValue("meta", "{}");
      await body.ExecuteNonQueryAsync();
    }
    return eventId;
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }
}
