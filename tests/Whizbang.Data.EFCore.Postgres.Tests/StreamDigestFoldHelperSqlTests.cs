// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2106: the one stream digest fold, <c>_wh_fold_stream_digests</c>, called directly. It folds, takes out or seeds a
/// set of events, every lane together, and refuses what it cannot fold correctly rather than writing a wrong digest.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/336_StreamDigestsFoldThroughOneHelper.sql</code-under-test>
/// <docs>resilience/stream-integrity#one-digest-fold</docs>
[Category("Integration")]
[Category("Shard2")]
public class StreamDigestFoldHelperSqlTests : EFCoreTestBase {
  private const string TYPE = "Whizbang.Tests.FoldHelperEvent, Whizbang.Tests";
  private static readonly DateTimeOffset _t0 = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
  private static readonly Guid[] _oneOrigin = [Guid.Empty];
  private static readonly string[] _oneTenant = ["tenant-a"];
  private static readonly string[] _oneType = [TYPE];

  private static async Task<NpgsqlConnection> _openAsync(DbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task<int> _foldAsync(NpgsqlConnection conn, string mode, Guid stream, params Guid[] eventIds) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT _wh_fold_stream_digests(@o, @t, @ty, @s, @e, @mode, @now)";
    cmd.Parameters.AddWithValue("o", eventIds.Select(_ => Guid.Empty).ToArray());
    cmd.Parameters.AddWithValue("t", eventIds.Select(_ => "tenant-a").ToArray());
    cmd.Parameters.AddWithValue("ty", eventIds.Select(_ => TYPE).ToArray());
    cmd.Parameters.AddWithValue("s", eventIds.Select(_ => stream).ToArray());
    cmd.Parameters.AddWithValue("e", eventIds);
    cmd.Parameters.AddWithValue(nameof(mode), mode);
    cmd.Parameters.AddWithValue("now", _t0);
    return (int)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<(long Lo, long Hi, long? IdLo, long? IdHi, int Count)?> _rowAsync(NpgsqlConnection conn, Guid stream) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT digest_lo, digest_hi, id_lo, id_hi, event_count FROM wh_stream_digests WHERE stream_id = @s";
    cmd.Parameters.AddWithValue("s", stream);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) {
      return null;
    }
    return (r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.GetInt32(4));
  }

  /// <summary>The expected lanes, computed in C#: the id halves are the uuid's bytes in network order.</summary>
  private static (long IdLo, long IdHi) _idLanes(params Guid[] ids) {
    long lo = 0, hi = 0;
    foreach (var id in ids) {
      var bytes = id.ToByteArray(bigEndian: true);
      lo ^= System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0, 8));
      hi ^= System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(8, 8));
    }
    return (lo, hi);
  }

  [Test]
  public async Task Fold_ThenUnfold_EveryLaneRoundTripsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;
    Guid[] ids = [TrackedGuid.New().Value, TrackedGuid.New().Value, TrackedGuid.New().Value];

    await Assert.That(await _foldAsync(conn, "fold", stream, ids)).IsEqualTo(1);
    var (idLo, idHi) = _idLanes(ids);
    var folded = (await _rowAsync(conn, stream))!.Value;
    await Assert.That(folded.IdLo).IsEqualTo(idLo).Because("the id lanes are the XOR of the ids' big-endian halves");
    await Assert.That(folded.IdHi).IsEqualTo(idHi);
    await Assert.That(folded.Count).IsEqualTo(3);

    await Assert.That(await _foldAsync(conn, "unfold", stream, ids[0])).IsEqualTo(1);
    var (restLo, restHi) = _idLanes(ids[1], ids[2]);
    var unfolded = (await _rowAsync(conn, stream))!.Value;
    await Assert.That(unfolded.IdLo).IsEqualTo(restLo).Because("XOR is self-inverse: the unfolded event's halves leave");
    await Assert.That(unfolded.IdHi).IsEqualTo(restHi);
    await Assert.That(unfolded.Count).IsEqualTo(2);

    await _foldAsync(conn, "fold", stream, ids[0]);
    var refolded = (await _rowAsync(conn, stream))!.Value;
    await Assert.That((refolded.Lo, refolded.Hi)).IsEqualTo((folded.Lo, folded.Hi))
      .Because("folding the event back restores the hash lanes exactly");
  }

  [Test]
  public async Task Seed_WritesOnlyWhereNoBucketExistsAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;
    var first = TrackedGuid.New().Value;

    await Assert.That(await _foldAsync(conn, "seed", stream, first)).IsEqualTo(1);
    await Assert.That(await _foldAsync(conn, "seed", stream, TrackedGuid.New().Value)).IsEqualTo(0)
      .Because("a seed never rewrites a bucket that exists");
    var (idLo, _) = _idLanes(first);
    await Assert.That((await _rowAsync(conn, stream))!.Value.IdLo).IsEqualTo(idLo);
  }

  [Test]
  public async Task NothingToFold_WritesNothingAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT _wh_fold_stream_digests(NULL, NULL, NULL, NULL, NULL, 'fold', NOW())";

    await Assert.That((int)(await cmd.ExecuteScalarAsync())!).IsEqualTo(0)
      .Because("a writer whose set is empty aggregates to NULL arrays, which fold nothing");
  }

  [Test]
  public async Task AnUnknownMode_IsRefusedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);

    await Assert.That(async () => await _foldAsync(conn, "add", TrackedGuid.New().Value, TrackedGuid.New().Value))
      .Throws<PostgresException>().Because("a misspelled mode must fail loudly, never fold the wrong way");
  }

  [Test]
  public async Task UnpairedArrays_AreRefusedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT _wh_fold_stream_digests(@o, @t, @ty, @s, @e, 'fold', NOW())";
    cmd.Parameters.AddWithValue("o", _oneOrigin);
    cmd.Parameters.AddWithValue("t", _oneTenant);
    cmd.Parameters.AddWithValue("ty", _oneType);
    cmd.Parameters.AddWithValue("s", (Guid[])[TrackedGuid.New().Value]);
    cmd.Parameters.AddWithValue("e", (Guid[])[TrackedGuid.New().Value, TrackedGuid.New().Value]);

    await Assert.That(async () => await cmd.ExecuteScalarAsync())
      .Throws<PostgresException>().Because("unnest pads unpaired arrays with NULL keys, which would fold into no bucket");
  }

  [Test]
  public async Task UnderAGenericPlan_TheUnfoldFindsItsBucketsByPrimaryKeyAsync() {
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    await using var conn = await dataSource.OpenConnectionAsync();
    var stream = TrackedGuid.New().Value;
    var id = TrackedGuid.New().Value;
    await _executeAsync(conn, $"""
      INSERT INTO wh_stream_digests (origin_service_id, scope_tenant, event_type, stream_id, digest_lo, digest_hi, event_count)
      SELECT '00000000-0000-0000-0000-000000000000'::uuid, 'tenant-a', '{TYPE}', gen_random_uuid(), 1, 1, 1
      FROM generate_series(1, 20000);
      ANALYZE wh_stream_digests;
      """);
    await _foldAsync(conn, "fold", stream, id);
    await _executeAsync(conn, """
      SET plan_cache_mode = force_generic_plan;
      LOAD 'auto_explain';
      SET auto_explain.log_min_duration = 0;
      SET auto_explain.log_analyze = on;
      SET auto_explain.log_timing = off;
      SET auto_explain.log_nested_statements = on;
      SET auto_explain.log_format = 'json';
      SET auto_explain.log_level = 'notice';
      """);
    var notices = new List<string>();
    conn.Notice += (_, e) => notices.Add(e.Notice.MessageText);

    await _foldAsync(conn, "unfold", stream, id);

    var plans = string.Join("\n", notices.Where(n => n.Contains("wh_stream_digests", StringComparison.Ordinal)));
    await Assert.That(plans).IsNotEmpty().Because("precondition: the unfold's statement was planned and logged");
    await Assert.That(plans).DoesNotContain("\"Seq Scan\"")
      .Because("the unfold finds each bucket by its key; a sequential scan of the digest table on every removal grows with the store");
  }

  // ── the heal: recompute buckets from their events and rewrite what drifted, every lane ──

  private static async Task<Guid> _storeAsync(NpgsqlConnection conn, Guid stream) {
    var id = TrackedGuid.New().Value;
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope,
                                  commit_sequence, created_at)
      VALUES (@id, @s, @s, 'Probe', (SELECT COALESCE(MAX(version), 0) + 1 FROM wh_event_store WHERE stream_id = @s),
              @type, jsonb_build_object('t', 'tenant-a'), nextval('wh_commit_seq'), NOW() - INTERVAL '2 hours')
      """;
    cmd.Parameters.AddWithValue(nameof(id), id);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("type", TYPE);
    await cmd.ExecuteNonQueryAsync();
    return id;
  }

  private static async Task<List<(string Kind, Guid Stream, long? StoredLo, long? RecomputedLo)>> _healAsync(
      NpgsqlConnection conn, Guid[] streams, bool fill) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT kind, stream_id, stored_lo, recomputed_lo
      FROM _wh_heal_stream_digests(@s, INTERVAL '1 hour', @fill) ORDER BY kind
      """;
    cmd.Parameters.AddWithValue("s", streams);
    cmd.Parameters.AddWithValue(nameof(fill), fill);
    var rows = new List<(string, Guid, long?, long?)>();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync()) {
      rows.Add((r.GetString(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3)));
    }
    return rows;
  }

  private static Task _ageDigestsAsync(NpgsqlConnection conn, Guid stream) =>
    _executeAsync(conn, $"UPDATE wh_stream_digests SET updated_at = NOW() - INTERVAL '2 hours' WHERE stream_id = '{stream}'");

  [Test]
  public async Task Heal_ADriftedBucket_IsRewrittenWithEveryLane_AndReportedWithBothValuesAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;
    var a = await _storeAsync(conn, stream);
    _ = await _storeAsync(conn, stream);   // stored, but missing from the bucket: drift
    await _foldAsync(conn, "fold", stream, a);
    await _ageDigestsAsync(conn, stream);
    var stored = (await _rowAsync(conn, stream))!.Value;

    var report = await _healAsync(conn, [stream], fill: false);

    await Assert.That(report.Count).IsEqualTo(1);
    await Assert.That(report[0].Kind).IsEqualTo("drift_updated");
    await Assert.That(report[0].StoredLo).IsEqualTo(stored.Lo).Because("the report names what was stored");
    await StoredDigestInvariant.AssertAgreeAsync(conn, stream);
    await Assert.That(report[0].RecomputedLo).IsEqualTo((await _rowAsync(conn, stream))!.Value.Lo)
      .Because("and what it was rewritten to");
  }

  [Test]
  public async Task Heal_ABucketWithoutIdLanes_IsFilledOnlyWhenAsked_AndReportedAsFilledNotDriftAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;
    var a = await _storeAsync(conn, stream);
    await _foldAsync(conn, "fold", stream, a);
    await _executeAsync(conn, $"UPDATE wh_stream_digests SET id_lo = NULL, id_hi = NULL WHERE stream_id = '{stream}'");
    await _ageDigestsAsync(conn, stream);

    await Assert.That(await _healAsync(conn, [stream], fill: false)).IsEmpty()
      .Because("the sweep's heal leaves id lanes to the bounded backfill; it rewrites only drift");
    var filled = await _healAsync(conn, [stream], fill: true);

    await Assert.That(filled.Select(f => f.Kind)).IsEquivalentTo(["filled"])
      .Because("filling lanes that were never computed is not drift");
    await StoredDigestInvariant.AssertAgreeAsync(conn, stream);
  }

  [Test]
  public async Task Heal_APhantomBucketIsRemoved_AndAMissingOneAdded_EachReportedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var phantom = TrackedGuid.New().Value;
    var missing = TrackedGuid.New().Value;
    await _foldAsync(conn, "fold", phantom, TrackedGuid.New().Value);   // no event behind it
    await _ageDigestsAsync(conn, phantom);
    await _storeAsync(conn, missing);                                     // an event with no bucket

    var report = await _healAsync(conn, [phantom, missing], fill: false);

    await Assert.That(report.ConvertAll(r => (r.Kind, r.Stream)))
      .IsEquivalentTo([("drift_added", missing), ("drift_removed", phantom)]);
    await StoredDigestInvariant.AssertAgreeAsync(conn, missing);
    await Assert.That(await _rowAsync(conn, phantom)).IsNull();
  }

  [Test]
  public async Task Heal_ABucketChangedInsideTheSettleWindow_IsLeftAloneAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = TrackedGuid.New().Value;
    await _storeAsync(conn, stream);
    await _foldAsync(conn, "fold", stream, TrackedGuid.New().Value);   // drifted, but just written
    await _executeAsync(conn, $"UPDATE wh_stream_digests SET updated_at = NOW() WHERE stream_id = '{stream}'");

    await Assert.That(await _healAsync(conn, [stream], fill: true)).IsEmpty()
      .Because("a bucket touched inside the settle window may be mid-fold; it is healed on a later pass");
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
  }
}
