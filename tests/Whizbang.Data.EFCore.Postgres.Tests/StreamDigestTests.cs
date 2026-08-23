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
/// Stream-integrity Phase A: on-demand two-lane XOR stream digests. The load-bearing property:
/// the SAME event ids fold to the SAME digest regardless of which side computes it or in what
/// order the rows arrived — so an origin's own-emissions digest and a consumer's
/// received-from-that-origin digest agree exactly when nothing was lost. Ephemeral and
/// at-most-once rows are excluded; events inside the settle window are ignored on both sides.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[NotInParallel("StreamDigests")]
[Category("Shard2")]
public class StreamDigestTests : EFCoreTestBase {

  private const string TENANT_A = "tenant-a";

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task Digests_OriginAndConsumerAgree_WithExclusionsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var coordinator = _coordinator(ctx);

    var origin = TrackedGuid.NewMedo().Value;
    var stream = TrackedGuid.NewMedo().Value;
    var e1 = TrackedGuid.NewMedo().Value;
    var e2 = TrackedGuid.NewMedo().Value;

    // The origin's OWN emissions (locally-originated: no origin stamp) — settled.
    await _seedAsync(conn, stream, 1, TENANT_A, "Contracts.TypeX", e1, aged: true);
    await _seedAsync(conn, stream, 2, TENANT_A, "Contracts.TypeX", e2, aged: true);
    // Exclusions on the origin side: ephemeral (flags&8), at-most-once, and a FRESH event.
    await _seedAsync(conn, stream, 3, TENANT_A, "Contracts.TypeX", TrackedGuid.NewMedo().Value, aged: true, flags: 8);
    await _seedAsync(conn, stream, 4, TENANT_A, "Contracts.TypeX", TrackedGuid.NewMedo().Value, aged: true,
      metadataJson: "{\"deliveryGuarantee\":1}");
    await _seedAsync(conn, stream, 5, TENANT_A, "Contracts.TypeX", TrackedGuid.NewMedo().Value, aged: false);

    var settle = TimeSpan.FromMinutes(60);
    var own = await coordinator.ComputeStreamDigestsAsync(null, ["Contracts.TypeX"], settle);
    await Assert.That(own.Count).IsEqualTo(1);
    await Assert.That(own[0].EventCount).IsEqualTo(2)
      .Because("ephemeral, at-most-once, and inside-settle-window rows are all excluded from the fold.");

    // The CONSUMER's copy of the SAME two events (one store per service in production — simulate
    // the consumer sequentially in this schema): received from the origin, inserted in the
    // OPPOSITE order on a different local version course. Same ids ⇒ same digest.
    await using (var clear = conn.CreateCommand()) {
      clear.CommandText = "DELETE FROM wh_event_store; DELETE FROM wh_event_body";
      await clear.ExecuteNonQueryAsync();
    }
    await _seedReceivedAsync(conn, stream, 11, TENANT_A, "Contracts.TypeX", e2, origin, aged: true);
    await _seedReceivedAsync(conn, stream, 12, TENANT_A, "Contracts.TypeX", e1, origin, aged: true);

    var received = await coordinator.ComputeStreamDigestsAsync(origin, ["Contracts.TypeX"], settle);
    await Assert.That(received.Count).IsEqualTo(1);
    await Assert.That(received[0].DigestLo).IsEqualTo(own[0].DigestLo)
      .Because("the SAME event ids fold to the SAME digest on both sides — arrival order and local " +
               "versions are irrelevant; identity is the whole content.");
    await Assert.That(received[0].DigestHi).IsEqualTo(own[0].DigestHi);
    await Assert.That(received[0].EventCount).IsEqualTo(2);

    // Divergence detection: drop one consumer row → digests disagree.
    await using (var del = conn.CreateCommand()) {
      del.CommandText = "DELETE FROM wh_event_store WHERE event_id = @e AND origin_service_id IS NOT NULL";
      del.Parameters.AddWithValue("e", e1);
      await del.ExecuteNonQueryAsync();
    }
    var damaged = await coordinator.ComputeStreamDigestsAsync(origin, ["Contracts.TypeX"], settle);
    await Assert.That(damaged[0].DigestLo).IsNotEqualTo(own[0].DigestLo)
      .Because("a missing event changes the fold — the audit names this (tenant, type, stream) bucket.");
  }

  [Test]
  public async Task ComputeTypeDigests_MatchesStreamRollUp_BitIdenticalAsync() {
    // The store-side type roll-up must be bit-identical to rolling the per-stream compute up in
    // C# — the two run on opposite sides of a manifest comparison, so any drift reads as false
    // divergence. XOR over a type's events equals XOR over its stream buckets (they partition).
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var coordinator = _coordinator(ctx);

    var streamA = TrackedGuid.NewMedo().Value;
    var streamB = TrackedGuid.NewMedo().Value;
    await _seedAsync(conn, streamA, 1, TENANT_A, "Contracts.RollX", TrackedGuid.NewMedo().Value, aged: true);
    await _seedAsync(conn, streamA, 2, TENANT_A, "Contracts.RollX", TrackedGuid.NewMedo().Value, aged: true);
    await _seedAsync(conn, streamB, 1, TENANT_A, "Contracts.RollX", TrackedGuid.NewMedo().Value, aged: true);
    await _seedAsync(conn, streamB, 2, TENANT_A, "Contracts.RollY", TrackedGuid.NewMedo().Value, aged: true);

    var settle = TimeSpan.FromMinutes(5);
    var types = new List<string> { "Contracts.RollX", "Contracts.RollY" };
    var storeRollUp = await coordinator.ComputeTypeDigestsAsync(null, types, settle);
    var csharpRollUp = IntegrityDigestMath.RollUpToTypes(
      await coordinator.ComputeStreamDigestsAsync(null, types, settle));

    await Assert.That(storeRollUp.Count).IsEqualTo(csharpRollUp.Count);
    for (var i = 0; i < storeRollUp.Count; i++) {
      await Assert.That(storeRollUp[i].EventType).IsEqualTo(csharpRollUp[i].EventType);
      await Assert.That(storeRollUp[i].TenantScope).IsEqualTo(csharpRollUp[i].TenantScope);
      await Assert.That(storeRollUp[i].DigestLo).IsEqualTo(csharpRollUp[i].DigestLo)
        .Because("the SQL GROUP BY (tenant, type) fold must be bit-identical to the C# roll-up of stream buckets.");
      await Assert.That(storeRollUp[i].DigestHi).IsEqualTo(csharpRollUp[i].DigestHi);
      await Assert.That(storeRollUp[i].EventCount).IsEqualTo(csharpRollUp[i].EventCount);
      await Assert.That(storeRollUp[i].StreamId).IsEqualTo(Guid.Empty)
        .Because("type-level rows carry the empty stream id — there is no per-stream materialization.");
    }
    await Assert.That(storeRollUp.Single(d => d.EventType == "Contracts.RollX").EventCount).IsEqualTo(3);
  }

  [Test]
  public async Task CoverageGaps_FindUncursoredPerspectiveHistoryAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var coordinator = _coordinator(ctx);

    var covered = TrackedGuid.NewMedo().Value;
    var uncovered = TrackedGuid.NewMedo().Value;
    var coveredEvent = TrackedGuid.NewMedo().Value;
    await _seedAsync(conn, covered, 1, TENANT_A, "Contracts.TypeX", coveredEvent, aged: true);
    await _seedAsync(conn, uncovered, 1, TENANT_A, "Contracts.TypeX", TrackedGuid.NewMedo().Value, aged: true);
    await _seedAsync(conn, uncovered, 2, TENANT_A, "Contracts.TypeX", TrackedGuid.NewMedo().Value, aged: true);

    // The perspective association + a cursor on ONE of the two streams.
    await using (var assoc = conn.CreateCommand()) {
      assoc.CommandText = @"
        INSERT INTO wh_message_associations (message_type, normalized_message_type, association_type, target_name, service_name)
        VALUES ('Contracts.TypeX', 'Contracts.TypeX', 'perspective', 'CoverageProbePerspective', 'test-svc')
        ON CONFLICT DO NOTHING";
      await assoc.ExecuteNonQueryAsync();
    }
    await using (var cursor = conn.CreateCommand()) {
      cursor.CommandText = @"
        INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id)
        VALUES (@stream, 'CoverageProbePerspective', @last)";
      cursor.Parameters.AddWithValue("stream", covered);
      cursor.Parameters.AddWithValue("last", coveredEvent);
      await cursor.ExecuteNonQueryAsync();
    }

    var gaps = await coordinator.GetPerspectiveCoverageGapsAsync(TimeSpan.FromMinutes(60), 100);

    var gap = gaps.Single(g => g.PerspectiveName == "CoverageProbePerspective");
    await Assert.That(gap.StreamId).IsEqualTo(uncovered)
      .Because("the cursored stream is covered; the stream the perspective NEVER folded is the gap.");
    await Assert.That(gap.EventCount).IsEqualTo(2);
  }

  [Test]
  public async Task OwnAuditedEventTypes_AreTheZeroGuidLane_DistinctAsync() {
    // The checkpoint heartbeat fans out to the topics of this origin's own emitted types — the
    // received-from-elsewhere lane must NOT contribute (those are another origin's topics), and
    // duplicates collapse (one topic per type regardless of stream count).
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var coordinator = _coordinator(ctx);

    await using (var seed = conn.CreateCommand()) {
      seed.CommandText = @"
        INSERT INTO wh_stream_digests (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count)
        VALUES
          ('00000000-0000-0000-0000-000000000000', 'tenant-a', 'Contracts.Own.TypeA', @s1, 1, 1, 1),
          ('00000000-0000-0000-0000-000000000000', 'tenant-b', 'Contracts.Own.TypeA', @s2, 2, 2, 1),
          ('00000000-0000-0000-0000-000000000000', '',         'Contracts.Own.TypeB', @s3, 3, 3, 1),
          (@origin,                                 'tenant-a', 'Contracts.Foreign.TypeC', @s4, 4, 4, 1)";
      seed.Parameters.AddWithValue("s1", TrackedGuid.NewMedo().Value);
      seed.Parameters.AddWithValue("s2", TrackedGuid.NewMedo().Value);
      seed.Parameters.AddWithValue("s3", TrackedGuid.NewMedo().Value);
      seed.Parameters.AddWithValue("s4", TrackedGuid.NewMedo().Value);
      seed.Parameters.AddWithValue("origin", TrackedGuid.NewMedo().Value);
      await seed.ExecuteNonQueryAsync();
    }

    var types = await coordinator.GetOwnAuditedEventTypesAsync();

    await Assert.That(types).IsEquivalentTo(["Contracts.Own.TypeA", "Contracts.Own.TypeB"])
      .Because("only the zero-guid (own emissions) lane feeds the heartbeat fan-out, deduplicated " +
               "across tenants and streams — received-lane types belong to ANOTHER origin's topics.");
  }

  // ── seeding ──────────────────────────────────────────────────────────────

  private static async Task _seedAsync(
      NpgsqlConnection conn, Guid streamId, int version, string tenant, string eventType, Guid eventId,
      bool aged, int flags = 0, string? metadataJson = null) {
    await using (var store = conn.CreateCommand()) {
      store.CommandText = $@"
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, flags, created_at)
        VALUES (@event, @stream, @stream, 'TestAggregate', @type, @scope::jsonb, @version, nextval('wh_commit_seq'), @flags,
                NOW() - INTERVAL '{(aged ? "2 hours" : "0 seconds")}')";
      store.Parameters.AddWithValue("event", eventId);
      store.Parameters.AddWithValue("stream", streamId);
      store.Parameters.AddWithValue("type", eventType);
      store.Parameters.AddWithValue("scope", $"{{\"t\":\"{tenant}\"}}");
      store.Parameters.AddWithValue("version", version);
      store.Parameters.AddWithValue("flags", flags);
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
  }

  private static async Task _seedReceivedAsync(
      NpgsqlConnection conn, Guid streamId, int version, string tenant, string eventType, Guid eventId,
      Guid originServiceId, bool aged) {
    await using var store = conn.CreateCommand();
    store.CommandText = $@"
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, flags, origin_service_id, origin_commit_sequence, created_at)
      VALUES (@event, @stream, @stream, 'TestAggregate', @type, @scope::jsonb, @version, nextval('wh_commit_seq'), 0, @origin, @version,
              NOW() - INTERVAL '{(aged ? "2 hours" : "0 seconds")}')";
    store.Parameters.AddWithValue("event", eventId);
    store.Parameters.AddWithValue("stream", streamId);
    store.Parameters.AddWithValue("type", eventType);
    store.Parameters.AddWithValue("scope", $"{{\"t\":\"{tenant}\"}}");
    store.Parameters.AddWithValue("version", version);
    store.Parameters.AddWithValue("origin", originServiceId);
    await store.ExecuteNonQueryAsync();
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }
}
