using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The CHUNK-BOUNDED local fold for stream-level manifest comparison. Observed live on a large
/// store: a consumer's stream-level compare folded its ENTIRE received lane to check one
/// 500-stream manifest chunk — the windowed path paged with an unbounded stream bound, and the
/// legacy fallback recomputed the whole store — and the fleet OOM-crashlooped within seconds of
/// each audit. The local side of a chunk comparison only ever needs digests for the streams THE
/// CHUNK NAMES, so this read is bounded by the chunk size no matter how large the lane is.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
[Category("Integration")]
[Category("Shard2")]
public class ChunkBoundedCompareSqlTests : EFCoreTestBase {

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static async Task _seedReceivedAsync(NpgsqlConnection conn, Guid origin, Guid streamId,
      Guid eventId, string eventType, long originSeq) {
    await using (var store = conn.CreateCommand()) {
      store.CommandText = """
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
           commit_sequence, flags, created_at, origin_service_id, origin_commit_sequence)
        VALUES (@event, @stream, @stream, 'TestAggregate', @type, 'null'::jsonb, @oseq,
                nextval('wh_commit_seq'), 0, NOW() - INTERVAL '2 hours', @origin, @oseq)
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

  [Test]
  public async Task ForChunk_FoldsOnlyTheNamedStreams_NeverTheLaneAsync() {
    // The load-bearing bound: a lane can hold a million streams; the chunk names 500. The fold
    // must read the named set — folding the lane to answer a chunk is the OOM this fixes.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    const string TYPE = "Contracts.ChunkBoundProbe";

    var inChunkA = Guid.NewGuid();
    var inChunkB = Guid.NewGuid();
    var offChunk = Guid.NewGuid();
    await _seedReceivedAsync(conn, origin, inChunkA, Guid.NewGuid(), TYPE, 10);
    await _seedReceivedAsync(conn, origin, inChunkB, Guid.NewGuid(), TYPE, 20);
    await _seedReceivedAsync(conn, origin, offChunk, Guid.NewGuid(), TYPE, 30);

    var digests = await coordinator.ComputeStreamDigestsForChunkAsync(
      origin, [inChunkA, inChunkB], sinceSequence: null, untilSequence: null, TimeSpan.FromHours(1));

    await Assert.That(digests).IsNotNull();
    await Assert.That(digests!.Select(d => d.StreamId).OrderBy(s => s).ToList())
      .IsEquivalentTo(new[] { inChunkA, inChunkB }.OrderBy(s => s).ToList())
      .Because("the local side of a chunk comparison needs the chunk's streams and nothing else");
  }

  [Test]
  public async Task ForChunk_HonorsTheSequenceWindowAsync() {
    // A windowed manifest compares window against window — an out-of-window event contributing
    // locally would fabricate an identity mismatch on a stream that is actually fine.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var stream = Guid.NewGuid();
    const string TYPE = "Contracts.ChunkWindowProbe";

    await _seedReceivedAsync(conn, origin, stream, Guid.NewGuid(), TYPE, 10);   // below window
    await _seedReceivedAsync(conn, origin, stream, Guid.NewGuid(), TYPE, 50);   // inside
    await _seedReceivedAsync(conn, origin, stream, Guid.NewGuid(), TYPE, 90);   // at/above until

    var digests = await coordinator.ComputeStreamDigestsForChunkAsync(
      origin, [stream], sinceSequence: 20, untilSequence: 90, TimeSpan.FromHours(1));

    await Assert.That(digests!.Count).IsEqualTo(1);
    await Assert.That(digests[0].EventCount).IsEqualTo(1)
      .Because("[20, 90) admits only sequence 50 — window-vs-window or the buckets are not comparable");
  }

  [Test]
  public async Task ForChunk_NullWindow_FoldsTheStreamsWholeHistoryAsync() {
    // The legacy-manifest fallback shape: full history, but still only for the named streams.
    await using var conn = await _openAsync();
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var origin = Guid.NewGuid();
    var stream = Guid.NewGuid();
    const string TYPE = "Contracts.ChunkFullProbe";

    await _seedReceivedAsync(conn, origin, stream, Guid.NewGuid(), TYPE, 5);
    await _seedReceivedAsync(conn, origin, stream, Guid.NewGuid(), TYPE, 500_000);

    var digests = await coordinator.ComputeStreamDigestsForChunkAsync(
      origin, [stream], sinceSequence: null, untilSequence: null, TimeSpan.FromHours(1));

    await Assert.That(digests!.Count).IsEqualTo(1);
    await Assert.That(digests[0].EventCount).IsEqualTo(2);
  }
}
