// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2103: every stream-level digest a comparison reads carries the id lanes (the XOR of each event id's two 64-bit
/// halves), so the one event a bucket is missing can be named: the stored table, the full recompute, the chunk-bounded
/// fold, the windowed fold and the fold of events held without an origin sequence. Each is the same lane function, so
/// they agree with each other and with the ids.
/// </summary>
/// <docs>resilience/stream-integrity#one-missing-event-is-named</docs>
[Category("Integration")]
[Category("Shard2")]
public class StreamDigestIdLaneReadsSqlTests : EFCoreTestBase {
  private const string TYPE = "Whizbang.Tests.IdLaneReadEvent, Whizbang.Tests";

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static (long Lo, long Hi) _idLanes(IEnumerable<Guid> ids) {
    long lo = 0, hi = 0;
    foreach (var id in ids) {
      var bytes = id.ToByteArray(bigEndian: true);
      lo ^= BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0, 8));
      hi ^= BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(8, 8));
    }
    return (lo, hi);
  }

  /// <summary>Stores events straight into the store, settled and stamped, and folds them through the shared helper.</summary>
  private static async Task<Guid[]> _storeAsync(NpgsqlConnection conn, Guid stream, Guid? origin, int count, bool sequenced = true) {
    var ids = Enumerable.Range(0, count).Select(_ => TrackedGuid.New().Value).ToArray();
    for (var i = 0; i < count; i++) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = """
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope,
                                    commit_sequence, created_at, origin_service_id, origin_commit_sequence)
        VALUES (@id, @s, @s, 'Probe', @v, @type, jsonb_build_object('t', 'tenant-a'), nextval('wh_commit_seq'),
                NOW() - INTERVAL '2 hours', @o, @oseq)
        """;
      cmd.Parameters.AddWithValue("id", ids[i]);
      cmd.Parameters.AddWithValue("s", stream);
      cmd.Parameters.AddWithValue("v", i + 1);
      cmd.Parameters.AddWithValue("type", TYPE);
      cmd.Parameters.Add(new NpgsqlParameter("o", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)origin ?? DBNull.Value });
      cmd.Parameters.Add(new NpgsqlParameter("oseq", NpgsqlTypes.NpgsqlDbType.Bigint) {
        Value = origin is not null && sequenced ? 100 + i : DBNull.Value
      });
      await cmd.ExecuteNonQueryAsync();
    }
    await using var fold = conn.CreateCommand();
    fold.CommandText = """
      SELECT _wh_fold_stream_digests(array_agg(COALESCE(@o, '00000000-0000-0000-0000-000000000000'::uuid)),
                                     array_agg('tenant-a'), array_agg(@type), array_agg(@s), array_agg(e), 'fold',
                                     NOW() - INTERVAL '2 hours')
      FROM unnest(@ids) AS e
      """;
    fold.Parameters.Add(new NpgsqlParameter("o", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)origin ?? DBNull.Value });
    fold.Parameters.AddWithValue("type", TYPE);
    fold.Parameters.AddWithValue("s", stream);
    fold.Parameters.AddWithValue("ids", ids);
    _ = await fold.ExecuteScalarAsync();
    return ids;
  }

  private static async Task _assertLanesAsync(StreamDigest? digest, IEnumerable<Guid> ids, string read) {
    var (lo, hi) = _idLanes(ids);
    await Assert.That(digest).IsNotNull().Because($"precondition: the {read} returned the bucket");
    await Assert.That(digest!.IdLo).IsEqualTo(lo).Because($"the {read} carries the id lanes");
    await Assert.That(digest.IdHi).IsEqualTo(hi);
  }

  [Test]
  public async Task TheTableRead_CarriesTheStoredIdLanesAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;
    var ids = await _storeAsync(conn, stream, origin: null, 3);

    var digests = await _coordinator(ctx).GetStreamDigestsAsync(null, [TYPE]);

    await _assertLanesAsync(digests.SingleOrDefault(d => d.StreamId == stream), ids, "table read");
  }

  [Test]
  public async Task TheRecompute_CarriesTheIdLanesAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;
    var ids = await _storeAsync(conn, stream, origin: null, 2);

    var digests = await _coordinator(ctx).ComputeStreamDigestsAsync(null, [TYPE], TimeSpan.FromHours(1));

    await _assertLanesAsync(digests.SingleOrDefault(d => d.StreamId == stream), ids, "recompute");
  }

  [Test]
  public async Task TheChunkFold_CarriesTheIdLanesOfItsWindowAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;
    var origin = TrackedGuid.New().Value;
    var ids = await _storeAsync(conn, stream, origin, 3);   // origin sequences 100, 101, 102

    var window = await _coordinator(ctx).ComputeStreamDigestsForChunkAsync(origin, [stream], 100, 102, TimeSpan.FromHours(1));
    var whole = await _coordinator(ctx).ComputeStreamDigestsForChunkAsync(origin, [stream], null, null, TimeSpan.FromHours(1));

    await _assertLanesAsync(window!.SingleOrDefault(), ids.Take(2), "windowed chunk fold");
    await _assertLanesAsync(whole!.SingleOrDefault(), ids, "full-history chunk fold");
  }

  [Test]
  public async Task TheFoldOfEventsHeldWithoutASequence_CarriesTheirIdLanesAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;
    var origin = TrackedGuid.New().Value;
    var ids = await _storeAsync(conn, stream, origin, 2, sequenced: false);

    var held = await _coordinator(ctx).ComputeUnsequencedReceivedDigestsAsync(origin, [TYPE], [stream], TimeSpan.FromHours(1));

    await _assertLanesAsync(held.SingleOrDefault(), ids, "sequence-less fold");
  }

  [Test]
  public async Task TheWindowedStreamAnswer_CarriesTheIdLanesAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;
    var ids = await _storeAsync(conn, stream, origin: null, 2);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = EFCoreWorkCoordinator<WorkCoordinationDbContext>.WindowedStreamDigestSql("public");
    cmd.Parameters.Add(new NpgsqlParameter("p_origin", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("p_types", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = new[] { TYPE } });
    cmd.Parameters.AddWithValue("p_since", 0L);
    cmd.Parameters.AddWithValue("p_until", long.MaxValue);
    cmd.Parameters.Add(new NpgsqlParameter("p_resume", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = DBNull.Value });
    cmd.Parameters.AddWithValue("p_limit", 10);
    cmd.Parameters.AddWithValue("p_settle", "3600 seconds");
    cmd.Parameters.Add(new NpgsqlParameter("p_digest_exempt", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) {
      Value = Whizbang.Core.SystemEvents.DigestExemptTypes.TypeNames.ToArray()
    });
    await using var reader = await cmd.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue();
    var (lo, hi) = _idLanes(ids);

    await Assert.That(reader.GetInt64(reader.GetOrdinal("id_lo"))).IsEqualTo(lo)
      .Because("the origin's windowed answer carries the id lanes the consumer XORs against");
    await Assert.That(reader.GetInt64(reader.GetOrdinal("id_hi"))).IsEqualTo(hi);
  }

  [Test]
  public async Task AStoredBucketWithoutIdLanes_ReadsAsNullAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;
    await _storeAsync(conn, stream, origin: null, 1);
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "UPDATE wh_stream_digests SET id_lo = NULL, id_hi = NULL WHERE stream_id = @s";
      cmd.Parameters.AddWithValue("s", stream);
      await cmd.ExecuteNonQueryAsync();
    }

    var digest = (await _coordinator(ctx).GetStreamDigestsAsync(null, [TYPE])).Single(d => d.StreamId == stream);

    await Assert.That(digest.IdLo).IsNull().Because("not yet backfilled: the compare falls back to whole-stream sync");
    await Assert.That(digest.IdHi).IsNull();
  }
}
