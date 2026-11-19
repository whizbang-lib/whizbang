// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// What slim stored metadata costs the outbox emit chain, the hottest write path (#1725, migration 285): the same batches
/// run through the chain as 271 defined it (the full metadata in the row, nothing staged), and through 285's under the
/// Full form (the full row plus the route id: the settings read and the route hash alone) and under the default Linked
/// form (the slim row plus the staged full form), on one database, alternating round by round so no side runs on a
/// warmer cache or a larger store. The method is <see cref="ArchivedStreamEmitCostScenarioTests"/>'s.
/// </summary>
/// <remarks>
/// <para>
/// Two batch shapes, both 500 events: 50 streams that already hold events taking 10 each, and 500 new streams taking one
/// each. Every event carries two hops as a dispatched event does (a Current hop with instance, topic, caller, trace parent,
/// policy trail and scope, and a causation hop), about 1 KB of hops. A perspective subscribes to the event, so the chain
/// does its whole job.
/// </para>
/// <para>
/// Reported per batch: shared blocks over the chain's tables (staging included), the server's execution time from
/// <c>EXPLAIN ANALYZE</c>, and WAL bytes; and per event the metadata bytes the row keeps. Medians of five rounds.
/// Only the row size gates: the slim row is under half the full row. The times and blocks are recorded on the docs page
/// (event-store#what-slim-metadata-costs).
/// </para>
/// <para>
/// Measured (five alternating rounds, medians, a shared build server): existing streams 34,061 blocks, 2.48 MB WAL, 87 ms
/// before; 33,925 blocks, 2.50 MB, 138 ms under Full; 35,784 blocks, 2.83 MB, 177 ms under Linked. New streams 38,245,
/// 2.73 MB, 98 ms; 38,275, 2.74 MB, 148 ms; 40,007, 3.09 MB, 203 ms. The row's metadata is 1,476 bytes an event before,
/// 1,520 under Full (the route id) and 490 under Linked, with 1,476 staged. The times are noisy on a shared server (the
/// before chain ranged from 80 to 178 ms over the five rounds); blocks, WAL and sizes are not.
/// </para>
/// </remarks>
/// <docs>data/event-store#what-slim-metadata-costs</docs>
[Category("Benchmark")]
[NotInParallel] // A measurement: runs alone so it measures the code, not its neighbors.
[Category("Performance")]
public class StoredMetadataEmitCostScenarioTests : EFCoreTestBase {
  private const int EVENTS = 500;
  private const int WARM_STREAMS = 50;
  private const int ROUNDS = 5;
  private const string EVENT_TYPE = "Perf.Metadata.SomethingHappened, Perf.Metadata";
  private const string FUNCTION_START = "CREATE OR REPLACE FUNCTION public._emit_event_store_chain(";
  private const string FUNCTION_END = "$$ LANGUAGE plpgsql;";

  private static readonly string[] _tables = [
    "wh_outbox", "wh_event_store", "wh_event_body", "wh_stream_digests", "wh_perspective_events", "wh_stream_lifecycle",
    "wh_event_metadata_staging_default",
  ];

  private static readonly DateTimeOffset _now = new(2026, 11, 1, 14, 5, 0, TimeSpan.Zero);

  private static readonly string _hops = StoredMetadataCorpus.HopsJson([
    StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders),
    new MessageHop {
      Type = HopType.Causation, ServiceInstance = StoredMetadataCorpus.Billing, Timestamp = _now.AddSeconds(-2),
      CausationType = "Perf.Metadata.DoSomething", CorrelationId = StoredMetadataCorpus.Correlation, Topic = "commands",
      CallerMemberName = "HandleAsync", CallerFilePath = "/src/Perf/DoSomethingReceptor.cs", CallerLineNumber = 77,
    },
  ]);

  [Test]
  [Timeout(1800000)]
  public async Task EmitChain_FullAgainstLinked_ReportAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    var recorder = new WorkCostRecorder(ConnectionString);
    var report = new PerformanceBaseline.Report(PerformanceBaseline.Load(PerformanceBaseline.DefaultPath),
      "Outbox emit chain: full metadata (271), 285 under Full, 285 under Linked (slim row, staged full form)");
    var before = _chainAsMigrationDefinedIt("271_");
    var after = _chainAsMigrationDefinedIt("285_");

    await _executeAsync(conn, $"""
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), '{EVENT_TYPE}', 'perspective', 'Perf.Metadata.Perspective', 'perf', '{EVENT_TYPE}', NOW(), NOW())
      """);
    var warm = new List<Guid[]>();
    for (var i = 0; i < 3 * ROUNDS; i++) {
      warm.Add(await _seedWarmStreamsAsync(conn));
    }
    await _executeAsync(conn, "ANALYZE");

    var variants = new (string Name, string Chain, string Form)[] { ("full_271", before, "Full"), ("full_285", after, "Full"), ("linked_285", after, "Linked") };
    var runs = variants.ToDictionary(v => v.Name, _ => new List<Shapes>());
    for (var round = 0; round < ROUNDS; round++) {
      for (var v = 0; v < variants.Length; v++) {
        await _executeAsync(conn, variants[v].Chain);
        await _executeAsync(conn, $"""
          INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('metadata_write_form', '{variants[v].Form}', 'string')
          ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
          """);
        runs[variants[v].Name].Add(await _measureBothShapesAsync(conn, recorder, warm[(3 * round) + v]));
      }
    }
    await _executeAsync(conn, after);
    await _executeAsync(conn, "DELETE FROM wh_settings WHERE setting_key = 'metadata_write_form'");

    foreach (var (shape, pick) in new (string, Func<Shapes, Shape>)[] { ("existing", s => s.Existing), ("new", s => s.New) }) {
      foreach (var (name, _, _) in variants) {
        var measured = runs[name].ConvertAll(s => pick(s));
        report.Measure($"stored_metadata.{shape}_streams.{name}.blocks", _median(measured, x => x.Blocks), "blocks/batch");
        report.Measure($"stored_metadata.{shape}_streams.{name}.ms", _median(measured, x => x.ExecutionMs), "ms/batch");
        report.Measure($"stored_metadata.{shape}_streams.{name}.wal_bytes", _median(measured, x => x.WalBytes), "bytes/batch");
        report.Measure($"stored_metadata.{shape}_streams.{name}.row_metadata_bytes", _median(measured, x => x.RowMetadataBytes), "bytes/event");
        report.Measure($"stored_metadata.{shape}_streams.{name}.staged_bytes", _median(measured, x => x.StagedBytes), "bytes/event");
      }
    }
    Console.WriteLine(report.Render());
    foreach (var (name, measured) in runs) {
      foreach (var run in measured) {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
          $"{name}: existing {run.Existing.Blocks} blocks {run.Existing.ExecutionMs:F1} ms {run.Existing.WalBytes} WAL {run.Existing.RowMetadataBytes:F0} B/row; " +
          $"new {run.New.Blocks} blocks {run.New.ExecutionMs:F1} ms {run.New.WalBytes} WAL {run.New.RowMetadataBytes:F0} B/row"));
      }
    }

    foreach (var run in runs.Values.SelectMany(r => r)) {
      await Assert.That(run.Existing.Stored).IsEqualTo((long)EVENTS);
      await Assert.That(run.New.Stored).IsEqualTo((long)EVENTS);
    }
    var fullRow = _median(runs["full_271"].ConvertAll(s => s.New), x => x.RowMetadataBytes);
    var slimRow = _median(runs["linked_285"].ConvertAll(s => s.New), x => x.RowMetadataBytes);
    await Assert.That(slimRow).IsLessThan(fullRow / 2).Because("the slim row keeps a fraction of the hops");
    await Assert.That(_median(runs["linked_285"].ConvertAll(s => s.New), x => x.StagedBytes)).IsGreaterThan(0d)
      .Because("control: under Linked the full form is staged");
    await Assert.That(_median(runs["full_285"].ConvertAll(s => s.New), x => x.StagedBytes)).IsEqualTo(0d);
  }

  private sealed record Shape(long Blocks, long Stored, double ExecutionMs, long WalBytes, double RowMetadataBytes, double StagedBytes);

  private sealed record Shapes(Shape Existing, Shape New);

  private static double _median(List<Shape> runs, Func<Shape, double> value) {
    var sorted = runs.Select(value).Order().ToList();
    return sorted[sorted.Count / 2];
  }

  private static async Task<Shapes> _measureBothShapesAsync(NpgsqlConnection conn, WorkCostRecorder recorder, Guid[] warm) {
    var existing = await _measureAsync(conn, recorder, [.. Enumerable.Range(0, EVENTS).Select(i => warm[i % warm.Length])]);
    var fresh = await _measureAsync(conn, recorder, [.. Enumerable.Range(0, EVENTS).Select(_ => (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New())]);
    return new Shapes(existing, fresh);
  }

  private static async Task<Shape> _measureAsync(NpgsqlConnection conn, WorkCostRecorder recorder, Guid[] streamPerEvent) {
    var (ids, instance) = await _queueAsync(conn, streamPerEvent);
    var executionMs = 0.0;
    var walStart = await _scalarAsync<string>(conn, "SELECT pg_current_wal_insert_lsn()::text", ids);
    var window = await recorder.MeasureAsync(conn, _tables, async () => executionMs = await _emitAsync(conn, ids, instance));
    var wal = await _scalarAsync<decimal>(conn, $"SELECT pg_wal_lsn_diff(pg_current_wal_insert_lsn(), '{walStart}')", ids);
    return new Shape(
      window.Tables.Values.Sum(t => t.Blocks),
      await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_event_store WHERE event_id = ANY(@ids)", ids),
      executionMs,
      (long)wal,
      (double)await _scalarAsync<decimal>(conn, "SELECT COALESCE(avg(pg_column_size(metadata)), 0) FROM wh_event_body WHERE event_id = ANY(@ids)", ids),
      (double)await _scalarAsync<decimal>(conn, "SELECT COALESCE(avg(pg_column_size(metadata)), 0) FROM wh_event_metadata_staging WHERE event_id = ANY(@ids)", ids));
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, Guid[] ids) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue(nameof(ids), NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
    return (T)Convert.ChangeType(await cmd.ExecuteScalarAsync(), typeof(T), CultureInfo.InvariantCulture)!;
  }

  private static async Task<Guid[]> _seedWarmStreamsAsync(NpgsqlConnection conn) {
    var streams = Enumerable.Range(0, WARM_STREAMS).Select(_ => (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New()).ToArray();
    var (ids, instance) = await _queueAsync(conn, [.. Enumerable.Range(0, WARM_STREAMS * 5).Select(i => streams[i % WARM_STREAMS])]);
    _ = await _emitAsync(conn, ids, instance);
    return streams;
  }

  private static async Task<(Guid[] Ids, Guid Instance)> _queueAsync(NpgsqlConnection conn, Guid[] streamPerEvent) {
    var instance = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New();
    var ids = streamPerEvent.Select(_ => (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New()).ToArray();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
         status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
      SELECT u.id, 'perf-topic', @type, 'env',
             jsonb_build_object('id', u.id::text, 'p', jsonb_build_object('orderId', u.id, 'note', repeat('n', 400)), 'h', @hops::jsonb),
             '{}'::jsonb, '{"t":"tenant-perf"}'::jsonb, 1, 0, @inst, @lease, 0, u.sid, true, 0, @now
      FROM unnest(@ids, @sids) AS u(id, sid)
      """;
    cmd.Parameters.AddWithValue("type", EVENT_TYPE);
    cmd.Parameters.AddWithValue("hops", NpgsqlDbType.Text, _hops);
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
