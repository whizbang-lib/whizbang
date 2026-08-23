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
/// at-most-once occurrences. A quiet window advances to an empty (but non-null) window, keeping
/// the liveness beat alive.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityCheckpoint.cs</code-under-test>
[Category("Integration")]
[NotInParallel("IntegrityCheckpoint")]
[Category("Shard3")]
public class IntegrityCheckpointAdvanceTests : EFCoreTestBase {

  private const string TENANT_A = "tenant-a";

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task Advance_BaselinesThenCountsThenIdlesAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _resetWatermarkAsync(conn);
    var coordinator = _coordinator(ctx);

    // History BEFORE the first advance must never be counted retroactively.
    await _seedAsync(conn, TrackedGuid.NewMedo().Value, 1, TENANT_A, "Contracts.OldHistory");

    var baseline = await coordinator.AdvanceIntegrityCheckpointAsync();
    await Assert.That(baseline).IsNotNull();
    await Assert.That(baseline!.FromCommitSequence).IsEqualTo(baseline.ToCommitSequence)
      .Because("the first run baselines at the current head — an empty window, no history storm.");
    await Assert.That(baseline.Buckets).IsEmpty();

    // A live window: 2× typeX + 1× typeY in tenant A, plus an at-most-once occurrence (excluded).
    var stream = TrackedGuid.NewMedo().Value;
    await _seedAsync(conn, stream, 2, TENANT_A, "Contracts.TypeX");
    await _seedAsync(conn, stream, 3, TENANT_A, "Contracts.TypeX");
    await _seedAsync(conn, stream, 4, TENANT_A, "Contracts.TypeY");
    await _seedAsync(conn, stream, 5, TENANT_A, "Contracts.OccurrenceFired",
      metadataJson: "{\"scheduleId\":\"s-1\",\"deliveryGuarantee\":1}");

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
    var stream = TrackedGuid.NewMedo().Value;
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

    var origin = TrackedGuid.NewMedo().Value;
    var otherOrigin = TrackedGuid.NewMedo().Value;
    var stream = TrackedGuid.NewMedo().Value;
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

  // ── seeding ──────────────────────────────────────────────────────────────

  /// <summary>Seeds one RECEIVED event: origin identity stamped, as the 1:1 forward persist does.</summary>
  private static async Task _seedReceivedAsync(
      NpgsqlConnection conn, Guid streamId, int version, string tenant, string eventType,
      Guid originServiceId, long originSeq) {
    var eventId = TrackedGuid.NewMedo().Value;
    await using var store = conn.CreateCommand();
    store.CommandText = @"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, flags, origin_service_id, origin_commit_sequence)
      VALUES (@event, @stream, @stream, 'TestAggregate', @type, @scope::jsonb, @version, nextval('wh_commit_seq'), 0, @origin, @oseq)";
    store.Parameters.AddWithValue("event", eventId);
    store.Parameters.AddWithValue("stream", streamId);
    store.Parameters.AddWithValue("type", eventType);
    store.Parameters.AddWithValue("scope", $"{{\"t\":\"{tenant}\"}}");
    store.Parameters.AddWithValue("version", version);
    store.Parameters.AddWithValue("origin", originServiceId);
    store.Parameters.AddWithValue("oseq", originSeq);
    await store.ExecuteNonQueryAsync();
  }

  private static async Task _resetWatermarkAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM wh_settings WHERE setting_key = 'integrity_checkpoint_watermark'";
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Seeds one stamped event (store pointer + body).</summary>
  private static async Task<Guid> _seedAsync(
      NpgsqlConnection conn, Guid streamId, int version, string tenant, string eventType,
      string? metadataJson = null) {
    var eventId = TrackedGuid.NewMedo().Value;
    await using (var store = conn.CreateCommand()) {
      store.CommandText = @"
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, flags)
        VALUES (@event, @stream, @stream, 'TestAggregate', @type, @scope::jsonb, @version, nextval('wh_commit_seq'), 0)";
      store.Parameters.AddWithValue("event", eventId);
      store.Parameters.AddWithValue("stream", streamId);
      store.Parameters.AddWithValue("type", eventType);
      store.Parameters.AddWithValue("scope", $"{{\"t\":\"{tenant}\"}}");
      store.Parameters.AddWithValue("version", version);
      await store.ExecuteNonQueryAsync();
    }
    await using (var body = conn.CreateCommand()) {
      body.CommandText = @"
        INSERT INTO wh_event_body (event_id, event_data, metadata)
        VALUES (@event, '{""seeded"":true}'::jsonb, @meta::jsonb)";
      body.Parameters.AddWithValue("event", eventId);
      body.Parameters.AddWithValue("meta", (object?)metadataJson ?? "{}");
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
