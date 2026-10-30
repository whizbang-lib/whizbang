// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What the archived-stream check (#1738, migration 271) adds to the outbox emit chain, the hottest write path: the same
/// batches run through the chain as 271 defines it (one probe of the sealed and archived streams' index answering both
/// refusals) and through the chain as 266 defined it (the probe of the sealed streams alone), on the same database,
/// alternating round by round so neither side runs on a warmer cache or a larger store. The method is the one
/// <see cref="StreamSealingEmitCostScenarioTests"/> used for the seal.
/// </summary>
/// <remarks>
/// <para>
/// Two batch shapes, both 500 events: 50 streams that already have ledger rows taking 10 events each, and 500 new streams
/// taking one event each. The ledger holds 2,000 sealed and 2,000 archived streams besides, so both partial indexes are
/// not empty. No stream in the batches is sealed or archived and none of their events is terminal: this is the price
/// every append pays, not the refusal path. A perspective subscribes to the event, so the chain does its whole job.
/// </para>
/// <para>
/// Blocks are counted per table over the call, and the check's own probe is measured on its own with
/// <c>EXPLAIN (ANALYZE, BUFFERS)</c>, because the ledger's block counts move between rounds by more than the probe costs.
/// The server's execution time from <c>EXPLAIN ANALYZE</c> is reported beside them and gates nothing.
/// </para>
/// <para>
/// Measured numbers are recorded on the docs page (archive-store#what-the-append-policy-costs).
/// </para>
/// </remarks>
/// <docs>fundamentals/events/archive-store#what-the-append-policy-costs</docs>
[Category("Benchmark")]
[Category("Performance")]
public class ArchivedStreamEmitCostScenarioTests : EFCoreTestBase {
  private const int EVENTS = 500;
  private const int WARM_STREAMS = 50;
  private const int SEALED_STREAMS = 2000;
  private const int ARCHIVED_STREAMS = 2000;
  private const int ROUNDS = 3;
  private const string EVENT_TYPE = "Perf.Archive.SomethingHappened, Perf.Archive";
  private const string FUNCTION_START = "CREATE OR REPLACE FUNCTION public._emit_event_store_chain(";
  private const string FUNCTION_END = "$$ LANGUAGE plpgsql;";

  private static readonly string[] _tables = [
    "wh_outbox", "wh_event_store", "wh_event_body", "wh_stream_digests", "wh_perspective_events", "wh_stream_lifecycle",
  ];

  private static readonly DateTimeOffset _now = new(2026, 10, 29, 14, 5, 0, TimeSpan.Zero);

  [Test]
  [Timeout(1800000)]
  public async Task EmitChain_With266sProbeAndWith271s_ReportAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    var recorder = new WorkCostRecorder(ConnectionString);
    var report = new PerformanceBaseline.Report(PerformanceBaseline.Load(PerformanceBaseline.DefaultPath),
      "Outbox emit chain, with the archived-stream check (271) and without it (266)");
    var withCheck = _chainAsMigrationDefinedIt("271_");
    var withoutCheck = _chainAsMigrationDefinedIt("266_");

    await _executeAsync(conn, $"""
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), '{EVENT_TYPE}', 'perspective', 'Perf.Archive.Perspective', 'perf', '{EVENT_TYPE}', NOW(), NOW())
      """);
    await _executeAsync(conn, $"""
      INSERT INTO wh_stream_lifecycle
        (stream_id, stream_type, first_appended_at, last_appended_bucket, last_version, state, sealed_at, sealed_reason)
      SELECT gen_random_uuid(), 'Perf.Archive.Closed', NOW(), NOW(), 1, 'Sealed', NOW(), 'Closed'
      FROM generate_series(1, {SEALED_STREAMS})
      """);
    await _executeAsync(conn, $"""
      INSERT INTO wh_stream_lifecycle
        (stream_id, stream_type, first_appended_at, last_appended_bucket, last_version, state)
      SELECT gen_random_uuid(), 'Perf.Archive.Gone', NOW(), NOW(), 3, 'Archived'
      FROM generate_series(1, {ARCHIVED_STREAMS})
      """);
    var warm = new List<Guid[]>();
    for (var i = 0; i < 2 * ROUNDS; i++) {
      warm.Add(await _seedWarmStreamsAsync(conn));
    }
    await _executeAsync(conn, "ANALYZE");

    var after = new List<Shapes>();
    var before = new List<Shapes>();
    for (var round = 0; round < ROUNDS; round++) {
      await _executeAsync(conn, withCheck);
      after.Add(await _measureBothShapesAsync(conn, recorder, warm[2 * round]));
      await _executeAsync(conn, withoutCheck);
      before.Add(await _measureBothShapesAsync(conn, recorder, warm[(2 * round) + 1]));
    }
    await _executeAsync(conn, withCheck);

    foreach (var (shape, pick) in new (string, Func<Shapes, Shape>)[] { ("existing", s => s.Existing), ("new", s => s.New) }) {
      var a = after.ConvertAll(s => pick(s));
      var b = before.ConvertAll(s => pick(s));
      report.Measure($"archive_check.{shape}_streams.blocks_before", _median(b, x => x.Blocks), "blocks/batch");
      report.Measure($"archive_check.{shape}_streams.blocks_after", _median(a, x => x.Blocks), "blocks/batch");
      report.Measure($"archive_check.{shape}_streams.ledger_blocks_before", _median(b, x => x.LedgerBlocks), "blocks/batch");
      report.Measure($"archive_check.{shape}_streams.ledger_blocks_after", _median(a, x => x.LedgerBlocks), "blocks/batch");
      report.Measure($"archive_check.{shape}_streams.ms_before", _median(b, x => x.ExecutionMs), "ms/batch");
      report.Measure($"archive_check.{shape}_streams.ms_after", _median(a, x => x.ExecutionMs), "ms/batch");

    }

    // The check alone, measured exactly: the chain's probe for each batch shape, with its buffers counted by the server.
    // The ledger's block counts above move by a few dozen blocks between rounds on their own (each round appends to
    // other streams), which hides a cost this small; this isolates it.
    foreach (var (shape, streams) in new (string, Guid[])[] {
      ("existing", warm[0]), ("new", [.. Enumerable.Range(0, EVENTS).Select(_ => (Guid)TrackedGuid.New())]),
    }) {
      var probe = await _probeBuffersAsync(conn, streams);
      report.Measure($"archive_check.{shape}_streams.probe_blocks", probe, "blocks/batch");
      await Assert.That(probe).IsLessThanOrEqualTo(2L * streams.Length)
        .Because("the check probes an index of the sealed and archived streams once per stream in the batch, never per event and never the event store");
    }
    Console.WriteLine(report.Render());
    foreach (var (side, runs) in new[] { ("after", after), ("before", before) }) {
      foreach (var run in runs) {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
          $"{side}: existing {run.Existing.Blocks} blocks ({run.Existing.LedgerBlocks} ledger) {run.Existing.ExecutionMs:F1} ms; " +
          $"new {run.New.Blocks} blocks ({run.New.LedgerBlocks} ledger) {run.New.ExecutionMs:F1} ms"));
      }
    }

    foreach (var run in after) {
      await Assert.That(run.Existing.Stored).IsEqualTo((long)EVENTS).Because("no stream in the batch is sealed or archived, so nothing is refused");
      await Assert.That(run.New.Stored).IsEqualTo((long)EVENTS);
    }
  }

  private sealed record Shape(long Blocks, long LedgerBlocks, long Stored, double ExecutionMs);

  private sealed record Shapes(Shape Existing, Shape New);

  private static double _median(List<Shape> runs, Func<Shape, double> value) {
    var sorted = runs.Select(value).Order().ToList();
    return sorted[sorted.Count / 2];
  }

  private static async Task<Shapes> _measureBothShapesAsync(NpgsqlConnection conn, WorkCostRecorder recorder, Guid[] warm) {
    var existing = await _measureAsync(conn, recorder, [.. Enumerable.Range(0, EVENTS).Select(i => warm[i % warm.Length])]);
    var fresh = await _measureAsync(conn, recorder, [.. Enumerable.Range(0, EVENTS).Select(_ => (Guid)TrackedGuid.New())]);
    return new Shapes(existing, fresh);
  }

  private static async Task<Shape> _measureAsync(NpgsqlConnection conn, WorkCostRecorder recorder, Guid[] streamPerEvent) {
    var (ids, instance) = await _queueAsync(conn, streamPerEvent);
    var executionMs = 0.0;
    var window = await recorder.MeasureAsync(conn, _tables, async () => executionMs = await _emitAsync(conn, ids, instance));
    await using var stored = conn.CreateCommand();
    stored.CommandText = "SELECT count(*) FROM wh_event_store WHERE event_id = ANY(@ids)";
    stored.Parameters.AddWithValue(nameof(ids), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
    return new Shape(
      window.Tables.Values.Sum(t => t.Blocks),
      window.Tables["wh_stream_lifecycle"].Blocks,
      Convert.ToInt64(await stored.ExecuteScalarAsync(), CultureInfo.InvariantCulture),
      executionMs);
  }

  private static async Task<Guid[]> _seedWarmStreamsAsync(NpgsqlConnection conn) {
    var streams = Enumerable.Range(0, WARM_STREAMS).Select(_ => (Guid)TrackedGuid.New()).ToArray();
    var (ids, instance) = await _queueAsync(conn, [.. Enumerable.Range(0, WARM_STREAMS * 5).Select(i => streams[i % WARM_STREAMS])]);
    _ = await _emitAsync(conn, ids, instance);
    return streams;
  }

  private static async Task<(Guid[] Ids, Guid Instance)> _queueAsync(NpgsqlConnection conn, Guid[] streamPerEvent) {
    var instance = (Guid)TrackedGuid.New();
    var ids = streamPerEvent.Select(_ => (Guid)TrackedGuid.New()).ToArray();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
         status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
      SELECT u.id, 'perf-topic', @type, 'env',
             jsonb_build_object('p', jsonb_build_object('orderId', u.id, 'note', repeat('n', 400))),
             '{}'::jsonb, '{"t":"tenant-perf"}'::jsonb, 1, 0, @inst, @lease, 0, u.sid, true, 0, @now
      FROM unnest(@ids, @sids) AS u(id, sid)
      """;
    cmd.Parameters.AddWithValue("type", EVENT_TYPE);
    cmd.Parameters.AddWithValue("inst", instance);
    cmd.Parameters.AddWithValue("lease", _now.AddMinutes(5));
    cmd.Parameters.AddWithValue("now", _now);
    cmd.Parameters.AddWithValue(nameof(ids), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
    cmd.Parameters.AddWithValue("sids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, streamPerEvent);
    _ = await cmd.ExecuteNonQueryAsync();
    return (ids, instance);
  }

  /// <summary>Runs the chain under EXPLAIN ANALYZE and returns the server's execution time for the call.</summary>
  private static async Task<double> _emitAsync(NpgsqlConnection conn, Guid[] ids, Guid instance) {
    await using var emit = conn.CreateCommand();
    emit.CommandText = "EXPLAIN (ANALYZE, FORMAT JSON) SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
    emit.Parameters.AddWithValue(nameof(ids), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
    emit.Parameters.AddWithValue("inst", instance);
    emit.Parameters.AddWithValue("lease", _now.AddMinutes(5));
    emit.Parameters.AddWithValue("now", _now);
    var plan = (string)(await emit.ExecuteScalarAsync())!;
    using var json = JsonDocument.Parse(plan);
    return json.RootElement[0].GetProperty("Execution Time").GetDouble();
  }

  /// <summary>The buffers the chain's probe of the sealed and archived streams reads for these streams, from EXPLAIN (ANALYZE, BUFFERS).</summary>
  private static async Task<long> _probeBuffersAsync(NpgsqlConnection conn, Guid[] streams) {
    await using var probe = conn.CreateCommand();
    probe.CommandText = """
      EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
      SELECT COALESCE(bool_or(l.sealed_at IS NOT NULL), false), COALESCE(bool_or(l.sealed_at IS NULL AND l.state = 'Archived'), false)
      FROM wh_stream_lifecycle l
      WHERE l.stream_id = ANY(@streams) AND (l.sealed_at IS NOT NULL OR l.state = 'Archived')
      """;
    probe.Parameters.AddWithValue(nameof(streams), NpgsqlDbType.Array | NpgsqlDbType.Uuid, streams);
    var plan = (string)(await probe.ExecuteScalarAsync())!;
    using var json = JsonDocument.Parse(plan);
    var top = json.RootElement[0].GetProperty("Plan");
    return top.GetProperty("Shared Hit Blocks").GetInt64() + top.GetProperty("Shared Read Blocks").GetInt64();
  }

  /// <summary>The outbox emit chain exactly as the named migration wrote it.</summary>
  private static string _chainAsMigrationDefinedIt(string migrationPrefix) {
    var sql = new PostgresMigrationProvider().GetMigrations().Single(m => m.Name.StartsWith(migrationPrefix, StringComparison.Ordinal)).Sql;
    var start = sql.IndexOf(FUNCTION_START, StringComparison.Ordinal);
    var end = sql.IndexOf(FUNCTION_END, start, StringComparison.Ordinal) + FUNCTION_END.Length;
    return sql[start..end];
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
