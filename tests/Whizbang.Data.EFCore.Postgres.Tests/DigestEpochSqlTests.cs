using System;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Epoch substrate for bounded integrity reconciliation (migration 092), against real Postgres.
///
/// <para>
/// Epochs partition each origin lane's commit-sequence space into fixed-width windows and store one
/// immutable XOR fold per <c>(origin, tenant, type, epoch)</c> bucket. Any sequence range then
/// composes by XOR of epoch folds — which is what lets reconciliation stop re-reading history: the
/// exchange cost becomes proportional to the open window, not to everything ever stored.
/// </para>
///
/// <para>
/// Deliberate design point these tests pin: epochs are derived at CLOSURE time by a range recompute
/// against the event store — the emit chain is untouched. The local lane's commit sequence is
/// stamped asynchronously (~1ms after emit), so epoch assignment at emit time is impossible for it;
/// deriving at closure keeps the hot path byte-identical and gives both lanes one mechanism.
/// </para>
///
/// <para>
/// Contract under test:
/// <c>close_digest_epochs(p_now, p_settle_seconds, p_max_epochs) RETURNS INT</c> — advances each
/// lane's contiguous frontier, folding every closable epoch into bucket rows. An epoch is closable
/// only when the lane's settled maximum sequence lies beyond it AND no unsettled event sits inside
/// its range — the second guard exists because a RECEIVED lane can get a fresh-arrived event with
/// an old origin sequence (redelivery), which a settled-max check alone would seal over.
/// <c>refold_digest_epochs(p_origin_lane, p_from, p_to) RETURNS INT</c> — recomputes closed epochs
/// after a repair back-fills events into their range. The zero UUID is the local lane, matching
/// the wh_stream_digests normalization.
/// </para>
/// </summary>
/// <docs>resilience/stream-integrity</docs>
[Category("Integration")]
public class DigestEpochSqlTests : EFCoreTestBase {

  private const string ZERO = "00000000-0000-0000-0000-000000000000";

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task _setWidthAsync(NpgsqlConnection conn, long width) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_settings (setting_key, setting_value, value_type, description)
      VALUES ('integrity_epoch_width', @w, 'integer', 'test epoch width')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """;
    cmd.Parameters.AddWithValue("w", width.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Seeds one LOCAL-lane event (origin_service_id NULL) with an explicit commit sequence.</summary>
  private static async Task _seedLocalAsync(NpgsqlConnection conn, Guid streamId, Guid eventId,
      string eventType, long commitSeq, bool settled, int flags = 0, string? tenant = null,
      int? deliveryGuarantee = null) {
    await using (var store = conn.CreateCommand()) {
      store.CommandText = $"""
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
           commit_sequence, flags, created_at)
        VALUES (@event, @stream, @stream, 'TestAggregate', @type, @scope::jsonb, @seq,
                @seq, @flags, NOW() - INTERVAL '{(settled ? "2 hours" : "0 seconds")}')
        """;
      store.Parameters.AddWithValue("event", eventId);
      store.Parameters.AddWithValue("stream", streamId);
      store.Parameters.AddWithValue("type", eventType);
      store.Parameters.AddWithValue("scope", tenant is null ? "null" : $"{{\"t\":\"{tenant}\"}}");
      store.Parameters.AddWithValue("seq", commitSeq);
      store.Parameters.AddWithValue("flags", flags);
      await store.ExecuteNonQueryAsync();
    }
    await using (var body = conn.CreateCommand()) {
      body.CommandText = """
        INSERT INTO wh_event_body (event_id, event_data, metadata)
        VALUES (@event, '{"seeded":true}'::jsonb, @meta::jsonb)
        """;
      body.Parameters.AddWithValue("event", eventId);
      body.Parameters.AddWithValue("meta",
        deliveryGuarantee is int g ? $"{{\"deliveryGuarantee\":{g}}}" : "{}");
      await body.ExecuteNonQueryAsync();
    }
  }

  /// <summary>Seeds one RECEIVED-lane event carrying the origin's stamp; the local commit sequence
  /// is unrelated to the origin sequence, exactly as with real cross-service delivery.</summary>
  private static async Task _seedReceivedAsync(NpgsqlConnection conn, Guid origin, Guid streamId,
      Guid eventId, string eventType, long originSeq, bool settled) {
    await using (var store = conn.CreateCommand()) {
      store.CommandText = $"""
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
           commit_sequence, flags, created_at, origin_service_id, origin_commit_sequence)
        VALUES (@event, @stream, @stream, 'TestAggregate', @type, 'null'::jsonb, @oseq,
                nextval('wh_commit_seq'), 0, NOW() - INTERVAL '{(settled ? "2 hours" : "0 seconds")}',
                @origin, @oseq)
        """;
      store.Parameters.AddWithValue("event", eventId);
      store.Parameters.AddWithValue("stream", streamId);
      store.Parameters.AddWithValue("type", eventType);
      store.Parameters.AddWithValue("origin", origin);
      store.Parameters.AddWithValue("oseq", originSeq);
      await store.ExecuteNonQueryAsync();
    }
    await using (var body = conn.CreateCommand()) {
      body.CommandText = """
        INSERT INTO wh_event_body (event_id, event_data, metadata)
        VALUES (@event, '{"seeded":true}'::jsonb, '{}'::jsonb)
        """;
      body.Parameters.AddWithValue("event", eventId);
      await body.ExecuteNonQueryAsync();
    }
  }

  private static async Task<int> _closeAsync(NpgsqlConnection conn, int settleSeconds = 3600, int maxEpochs = 100) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT close_digest_epochs(NOW(), @settle, @max)";
    cmd.Parameters.AddWithValue("settle", settleSeconds);
    cmd.Parameters.AddWithValue("max", maxEpochs);
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<(long Lo, long Hi, int Count)?> _epochRowAsync(NpgsqlConnection conn,
      string originLane, string tenant, string eventType, long epochId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT digest_lo, digest_hi, event_count FROM wh_digest_epochs
      WHERE origin_service_id = @o::uuid AND scope_tenant = @t AND event_type = @ty AND epoch_id = @e
      """;
    cmd.Parameters.AddWithValue("o", originLane);
    cmd.Parameters.AddWithValue("t", tenant);
    cmd.Parameters.AddWithValue("ty", eventType);
    cmd.Parameters.AddWithValue("e", epochId);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return null;
    }
    return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2));
  }

  /// <summary>The expected fold, computed by the same primitive the production SQL uses.</summary>
  private static async Task<(long Lo, long Hi)> _expectedFoldAsync(NpgsqlConnection conn, params Guid[] eventIds) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT bit_xor(hashtextextended(x::text, 0)), bit_xor(hashtextextended(x::text, 1))
      FROM unnest(@ids::uuid[]) AS x
      """;
    cmd.Parameters.AddWithValue("ids", eventIds);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (reader.GetInt64(0), reader.GetInt64(1));
  }

  [Test]
  public async Task CloseDigestEpochs_SettledLowerEpoch_ClosesWithCorrectFoldAsync() {
    await using var conn = await _openAsync();
    await _setWidthAsync(conn, 100);
    var stream = Guid.NewGuid();
    var e1 = Guid.NewGuid();
    var e2 = Guid.NewGuid();

    // Epoch 0 = sequences [0, 100): two settled events. Epoch 1 holds the settled maximum (105),
    // so epoch 1 itself stays OPEN — the lane will keep appending into it — and only epoch 0 closes.
    await _seedLocalAsync(conn, stream, e1, "Contracts.EpochProbe", 5, settled: true);
    await _seedLocalAsync(conn, stream, e2, "Contracts.EpochProbe", 10, settled: true);
    await _seedLocalAsync(conn, stream, Guid.NewGuid(), "Contracts.EpochProbe", 105, settled: true);

    var closed = await _closeAsync(conn);

    await Assert.That(closed).IsGreaterThanOrEqualTo(1)
      .Because("epoch 0 is fully settled and below the frontier target — it must close");

    var row = await _epochRowAsync(conn, ZERO, "", "Contracts.EpochProbe", 0);
    await Assert.That(row).IsNotNull()
      .Because("closing must materialize the bucket-epoch fold row");
    var expected = await _expectedFoldAsync(conn, e1, e2);
    await Assert.That(row!.Value.Lo).IsEqualTo(expected.Lo)
      .Because("the fold must be the XOR of exactly the epoch's events — lane 0");
    await Assert.That(row.Value.Hi).IsEqualTo(expected.Hi)
      .Because("and lane 1; a wrong fold silently corrupts every future range comparison");
    await Assert.That(row.Value.Count).IsEqualTo(2);

    await Assert.That(await _epochRowAsync(conn, ZERO, "", "Contracts.EpochProbe", 1)).IsNull()
      .Because("the epoch holding the settled maximum stays open — later events land in it");
  }

  [Test]
  public async Task CloseDigestEpochs_FreshArrivalWithOldOriginSequence_BlocksThatEpochAsync() {
    // The guard that makes closure safe for RECEIVED lanes: redelivery can land a fresh event with
    // an OLD origin sequence. A settled-max frontier alone would close over it and the fold would
    // be stale from birth. An epoch with any unsettled event in range must not close — and because
    // the frontier is contiguous, closure stalls there until the arrival settles.
    await using var conn = await _openAsync();
    await _setWidthAsync(conn, 100);
    var origin = Guid.NewGuid();
    var stream = Guid.NewGuid();

    await _seedReceivedAsync(conn, origin, stream, Guid.NewGuid(), "Contracts.EpochProbe", 5, settled: true);
    await _seedReceivedAsync(conn, origin, stream, Guid.NewGuid(), "Contracts.EpochProbe", 50, settled: false); // fresh, old seq
    await _seedReceivedAsync(conn, origin, stream, Guid.NewGuid(), "Contracts.EpochProbe", 150, settled: true);

    _ = await _closeAsync(conn);

    await Assert.That(await _epochRowAsync(conn, origin.ToString(), "", "Contracts.EpochProbe", 0)).IsNull()
      .Because("an unsettled event inside the range means the fold would be incomplete — the epoch must wait");
  }

  [Test]
  public async Task CloseDigestEpochs_EphemeralAndAtMostOnce_ExcludedFromFoldAsync() {
    // Regression lock for the proposal's open question 3, settled by reading the emit chain:
    // ephemeral-born events (flags & 8) never enter the fold, so the reaper and the tier-2 pointer
    // prune are NOT seal-invalidation sites. At-most-once occurrences are excluded for the same
    // reason they are excluded from the running digests: they may legitimately never arrive.
    await using var conn = await _openAsync();
    await _setWidthAsync(conn, 100);
    var stream = Guid.NewGuid();
    var kept = Guid.NewGuid();

    await _seedLocalAsync(conn, stream, kept, "Contracts.EpochProbe", 5, settled: true);
    await _seedLocalAsync(conn, stream, Guid.NewGuid(), "Contracts.EpochProbe", 6, settled: true, flags: 8);
    await _seedLocalAsync(conn, stream, Guid.NewGuid(), "Contracts.EpochProbe", 7, settled: true, deliveryGuarantee: 1);
    await _seedLocalAsync(conn, stream, Guid.NewGuid(), "Contracts.EpochProbe", 105, settled: true);

    _ = await _closeAsync(conn);

    var row = await _epochRowAsync(conn, ZERO, "", "Contracts.EpochProbe", 0);
    await Assert.That(row).IsNotNull();
    var expected = await _expectedFoldAsync(conn, kept);
    await Assert.That(row!.Value.Count).IsEqualTo(1)
      .Because("ephemeral and at-most-once events must not be counted — they are outside the audited set");
    await Assert.That(row.Value.Lo).IsEqualTo(expected.Lo)
      .Because("and must not be folded — otherwise every reap would corrupt a sealed epoch");
  }

  [Test]
  public async Task CloseDigestEpochs_ReceivedLane_KeysOnOriginSequenceAsync() {
    // The received lane's epochs are keyed by the ORIGIN's sequence, not the local one — the local
    // commit sequence of a received event is unrelated to its position in the origin's history.
    await using var conn = await _openAsync();
    await _setWidthAsync(conn, 100);
    var origin = Guid.NewGuid();
    var stream = Guid.NewGuid();
    var e1 = Guid.NewGuid();
    var e2 = Guid.NewGuid();

    await _seedReceivedAsync(conn, origin, stream, e1, "Contracts.EpochProbe", 3, settled: true);
    await _seedReceivedAsync(conn, origin, stream, e2, "Contracts.EpochProbe", 7, settled: true);
    await _seedReceivedAsync(conn, origin, stream, Guid.NewGuid(), "Contracts.EpochProbe", 130, settled: true);

    _ = await _closeAsync(conn);

    var row = await _epochRowAsync(conn, origin.ToString(), "", "Contracts.EpochProbe", 0);
    await Assert.That(row).IsNotNull()
      .Because("each origin gets its own lane with its own frontier");
    var expected = await _expectedFoldAsync(conn, e1, e2);
    await Assert.That(row!.Value.Lo).IsEqualTo(expected.Lo);
    await Assert.That(row.Value.Count).IsEqualTo(2);

    await Assert.That(await _epochRowAsync(conn, ZERO, "", "Contracts.EpochProbe", 0)).IsNull()
      .Because("received events must not leak into the local lane's epochs");
  }

  [Test]
  public async Task CloseDigestEpochs_SecondCall_IsIdempotentAsync() {
    await using var conn = await _openAsync();
    await _setWidthAsync(conn, 100);
    var stream = Guid.NewGuid();
    var e1 = Guid.NewGuid();

    await _seedLocalAsync(conn, stream, e1, "Contracts.EpochProbe", 5, settled: true);
    await _seedLocalAsync(conn, stream, Guid.NewGuid(), "Contracts.EpochProbe", 105, settled: true);

    _ = await _closeAsync(conn);
    var first = await _epochRowAsync(conn, ZERO, "", "Contracts.EpochProbe", 0);
    var second = await _closeAsync(conn);
    var after = await _epochRowAsync(conn, ZERO, "", "Contracts.EpochProbe", 0);

    await Assert.That(second).IsEqualTo(0)
      .Because("nothing new became closable — a second pass must not re-close or double-fold");
    await Assert.That(after!.Value.Lo).IsEqualTo(first!.Value.Lo)
      .Because("closed rows are immutable outside an explicit refold");
    await Assert.That(after.Value.Count).IsEqualTo(first.Value.Count);
  }

  [Test]
  public async Task RefoldDigestEpochs_LateArrivalIntoClosedEpoch_MatchesExpectedAsync() {
    // The repair path: after a backfill delivers events whose origin sequence falls inside an
    // already-closed epoch, the repairer refolds that range explicitly. The nightly self-sweep is
    // the backstop for anything that skips this call.
    await using var conn = await _openAsync();
    await _setWidthAsync(conn, 100);
    var stream = Guid.NewGuid();
    var e1 = Guid.NewGuid();
    var late = Guid.NewGuid();

    await _seedLocalAsync(conn, stream, e1, "Contracts.EpochProbe", 5, settled: true);
    await _seedLocalAsync(conn, stream, Guid.NewGuid(), "Contracts.EpochProbe", 105, settled: true);
    _ = await _closeAsync(conn);

    await _seedLocalAsync(conn, stream, late, "Contracts.EpochProbe", 9, settled: true);

    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT refold_digest_epochs(@lane::uuid, 0, 0)";
      cmd.Parameters.AddWithValue("lane", ZERO);
      var refolded = (int)(await cmd.ExecuteScalarAsync())!;
      await Assert.That(refolded).IsGreaterThanOrEqualTo(1)
        .Because("the closed epoch's range gained an event — the refold must touch it");
    }

    var row = await _epochRowAsync(conn, ZERO, "", "Contracts.EpochProbe", 0);
    var expected = await _expectedFoldAsync(conn, e1, late);
    await Assert.That(row!.Value.Lo).IsEqualTo(expected.Lo)
      .Because("after refold the epoch reflects the repaired reality, not the stale pre-repair fold");
    await Assert.That(row.Value.Count).IsEqualTo(2);
  }

  [Test]
  public async Task EpochWidth_PinnedAtFirstClose_LaterSettingChangeIsIgnoredAsync() {
    // Epoch identity is floor(seq / width): changing the width remaps every epoch boundary and
    // makes existing folds meaningless. The width is therefore pinned per lane at first close;
    // changing the setting afterwards must not shift the boundaries of an existing lane.
    await using var conn = await _openAsync();
    await _setWidthAsync(conn, 100);
    var stream = Guid.NewGuid();

    await _seedLocalAsync(conn, stream, Guid.NewGuid(), "Contracts.EpochProbe", 5, settled: true);
    await _seedLocalAsync(conn, stream, Guid.NewGuid(), "Contracts.EpochProbe", 105, settled: true);
    _ = await _closeAsync(conn);
    await Assert.That(await _epochRowAsync(conn, ZERO, "", "Contracts.EpochProbe", 0)).IsNotNull();

    // Sabotage the setting, extend history, close again: epoch 1 must close at the ORIGINAL width.
    await _setWidthAsync(conn, 10);
    await _seedLocalAsync(conn, stream, Guid.NewGuid(), "Contracts.EpochProbe", 150, settled: true);
    await _seedLocalAsync(conn, stream, Guid.NewGuid(), "Contracts.EpochProbe", 205, settled: true);
    _ = await _closeAsync(conn);

    await Assert.That(await _epochRowAsync(conn, ZERO, "", "Contracts.EpochProbe", 1)).IsNotNull()
      .Because("epoch 1 under the pinned width [100,200) is closable once the frontier passes it");
  }

  [Test]
  public async Task Coordinator_CloseDigestEpochsAsync_DrivesTheSqlFunctionAsync() {
    // The C# seam MaintenanceWorker rides. The Core default returns the "unsupported" sentinel, so
    // this pins that the Postgres coordinator actually overrides it and reaches the function — a
    // wiring mistake here would leave the substrate inert while every unit test stayed green
    // (the fake-channel lesson: prove the live effect, not the enqueue).
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
    await _setWidthAsync(conn, 100);
    var stream = Guid.NewGuid();

    await _seedLocalAsync(conn, stream, Guid.NewGuid(), "Contracts.EpochProbe", 7, settled: true);
    await _seedLocalAsync(conn, stream, Guid.NewGuid(), "Contracts.EpochProbe", 130, settled: true);

    var closed = await coordinator.CloseDigestEpochsAsync(settleSeconds: 3600, maxEpochs: 100);

    await Assert.That(closed).IsGreaterThanOrEqualTo(1)
      .Because("epoch 0 was closable, and the coordinator must have actually run the closure to know that");
    await Assert.That(await _epochRowAsync(conn, ZERO, "", "Contracts.EpochProbe", 0)).IsNotNull()
      .Because("the live effect — a persisted epoch row — is the proof the call reached the database");
  }
}
