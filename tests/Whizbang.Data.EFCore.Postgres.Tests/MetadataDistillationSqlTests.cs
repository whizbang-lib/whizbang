// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Distillation (#1726, migration 286): staged full metadata becomes one route per path (counted, never rewritten) and
/// one trace per event; the row plus its trace plus its route rebuild the full metadata losslessly; and each of the
/// staged rows, the traces and the routes is kept for its own window.
/// </summary>
/// <docs>data/event-store#distillation</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/286_RoutesAndTraces.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/MetadataDistillationStep.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class MetadataDistillationSqlTests : EFCoreTestBase {
  private const string ORDER_PLACED = "MyApp.Orders.OrderPlaced, MyApp";
  private const string ORDER_SHIPPED = "MyApp.Orders.OrderShipped, MyApp";

  // ===== Distillation =====

  [Test]
  public async Task Distill_WritesRoutesAndTraces_AndConsumesTheStagedRowsAsync() {
    await using var conn = await _openAsync();
    var placed = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };
    var shipped = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Billing) };
    var a = await _appendAsync(conn, ORDER_PLACED, placed);
    var b = await _appendAsync(conn, ORDER_SHIPPED, shipped);

    var result = await _distillAsync(conn, 100);

    await Assert.That(result.Consumed).IsEqualTo(2L);
    await Assert.That(result.RoutesAdded).IsEqualTo(2L);
    await Assert.That(result.Traces).IsEqualTo(2L);
    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_event_metadata_staging")).IsEqualTo(0L).Because("a distilled staged row is removed");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT occurrences FROM wh_routes WHERE route_id = @r", ("r", RouteNormalizer.RouteId(ORDER_PLACED, placed))))
      .IsEqualTo(1L);
    await Assert.That(await _scalarAsync<string>(conn, "SELECT event_type FROM wh_routes WHERE route_id = @r", ("r", RouteNormalizer.RouteId(ORDER_SHIPPED, shipped))))
      .IsEqualTo("MyApp.Orders.OrderShipped");
    await Assert.That(await _scalarAsync<string>(conn, "SELECT hops -> 0 ->> 'cl' FROM wh_routes WHERE route_id = @r", ("r", RouteNormalizer.RouteId(ORDER_PLACED, placed))))
      .IsEqualTo("PlaceOrderReceptor");
    await Assert.That(await _scalarAsync<Guid>(conn, "SELECT correlation_id FROM wh_traces WHERE event_id = @e", ("e", a)))
      .IsEqualTo(StoredMetadataCorpus.Correlation.Value);
    await Assert.That(await _scalarAsync<Guid>(conn, "SELECT causation_id FROM wh_traces WHERE event_id = @e", ("e", b)))
      .IsEqualTo(StoredMetadataCorpus.Cause.Value);
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT NOT (hops -> 0 ? 'to') AND NOT (hops -> 0 -> 'si' ? 'sn') FROM wh_traces WHERE event_id = @e", ("e", a)))
      .IsTrue().Because("a trace keeps only what varies per message; the route holds the topic and the service");
    await Assert.That(await _scalarAsync<string>(conn, "SELECT metadata ->> 'rid' FROM wh_event_body WHERE event_id = @e", ("e", a)))
      .IsEqualTo(RouteNormalizer.RouteId(ORDER_PLACED, placed).ToString()).Because("no event row is touched: its route id was stamped at write time");
  }

  [Test]
  public async Task Distill_ARepeatedRoute_OnlyIncrementsItsCountersAsync() {
    await using var conn = await _openAsync();
    var first = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };
    var samePathOtherPod = new List<MessageHop> {
      StoredMetadataCorpus.RichHop(StoredMetadataCorpus.OrdersOtherPod, line: 99, traceParent: "00-1af7651916cd43dd8448eb211c80319c-c7ad6b7169203331-01"),
    };
    var routeId = RouteNormalizer.RouteId(ORDER_PLACED, first);
    await _appendAsync(conn, ORDER_PLACED, first);
    await _distillAsync(conn, 100);
    var hopsBefore = await _scalarAsync<string>(conn, "SELECT hops::text FROM wh_routes WHERE route_id = @r", ("r", routeId));

    await _appendAsync(conn, ORDER_PLACED, samePathOtherPod);
    var second = await _distillAsync(conn, 100);

    await Assert.That(RouteNormalizer.RouteId(ORDER_PLACED, samePathOtherPod)).IsEqualTo(routeId).Because("control: another pod and line is the same route");
    await Assert.That(second.RoutesAdded).IsEqualTo(0L);
    await Assert.That(second.RoutesUpdated).IsEqualTo(1L);
    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_routes")).IsEqualTo(1L).Because("a repeated route adds no route");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT occurrences FROM wh_routes WHERE route_id = @r", ("r", routeId))).IsEqualTo(2L);
    await Assert.That(await _scalarAsync<string>(conn, "SELECT hops::text FROM wh_routes WHERE route_id = @r", ("r", routeId))).IsEqualTo(hopsBefore)
      .Because("the route's path is written once; a repeat only counts");
    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_traces")).IsEqualTo(2L).Because("every occurrence has its own trace");
  }

  [Test]
  public async Task Distill_AddsDurationStatistics_InOneUpdatePerRouteAsync() {
    await using var conn = await _openAsync();
    List<MessageHop> withDuration(double ms) => [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) with { Duration = TimeSpan.FromMilliseconds(ms) }];
    var routeId = RouteNormalizer.RouteId(ORDER_PLACED, withDuration(1));
    foreach (var ms in new[] { 0.5, 5, 50, 500, 5000, 50000 }) {
      await _appendAsync(conn, ORDER_PLACED, withDuration(ms));
    }
    await _appendAsync(conn, ORDER_PLACED, [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) with { Duration = TimeSpan.Zero }]);

    await using var tx = await conn.BeginTransactionAsync();
    var (insertedBefore, updatedBefore) = await _routeWritesAsync(conn);
    var result = await _distillAsync(conn, 100);
    var (insertedAfter, updatedAfter) = await _routeWritesAsync(conn);
    await tx.CommitAsync();

    await Assert.That(result.Consumed).IsEqualTo(7L);
    await Assert.That(insertedAfter - insertedBefore + updatedAfter - updatedBefore).IsEqualTo(1L)
      .Because("seven occurrences of one route are one write to it, never one per event");
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT duration_count, duration_sum_ms, duration_min_ms, duration_max_ms, duration_buckets, occurrences FROM wh_routes WHERE route_id = @r";
    cmd.Parameters.AddWithValue("r", routeId);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    await Assert.That(reader.GetInt64(0)).IsEqualTo(6L).Because("an occurrence with no duration is counted as an occurrence but not as a duration");
    await Assert.That(reader.GetDouble(1)).IsEqualTo(55555.5);
    await Assert.That(reader.GetDouble(2)).IsEqualTo(0.5);
    await Assert.That(reader.GetDouble(3)).IsEqualTo(50000d);
    await Assert.That(reader.GetFieldValue<long[]>(4)).IsEquivalentTo(new long[] { 1, 1, 1, 1, 1, 1 })
      .Because("one occurrence in each bucket: up to 1 ms, 10 ms, 100 ms, 1 s, 10 s, and longer");
    await Assert.That(reader.GetInt64(5)).IsEqualTo(7L);
  }

  [Test]
  public async Task Distill_TakesAtMostTheBatch_OldestFirstAsync() {
    await using var conn = await _openAsync();
    var ids = new List<Guid>();
    for (var i = 0; i < 3; i++) {
      ids.Add(await _appendAsync(conn, ORDER_PLACED, [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]));
    }

    var result = await _distillAsync(conn, 2);

    await Assert.That(result.Consumed).IsEqualTo(2L);
    await Assert.That(await _scalarAsync<Guid>(conn, "SELECT event_id FROM wh_event_metadata_staging")).IsEqualTo(ids.Max())
      .Because("a batch takes the oldest event ids first, so the newest is the one left");
  }

  [Test]
  public async Task Distill_UnderSlimOrFull_ConsumesNothingAsync() {
    await using var conn = await _openAsync();
    await _appendAsync(conn, ORDER_PLACED, [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]);

    foreach (var form in new[] { "Slim", "Full" }) {
      await _settingAsync(conn, "metadata_write_form", form);
      var result = await _distillAsync(conn, 100);
      await Assert.That(result.Consumed).IsEqualTo(0L);
      await Assert.That(result.Status).IsEqualTo($"skipped (metadata_write_form={form})");
    }
    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_event_metadata_staging")).IsEqualTo(1L)
      .Because("Slim drops the staged form without distilling it; what was staged ages out by retention");
  }

  [Test]
  public async Task Distill_MalformedValues_ReadAsNothing_AndNeverStopTheBatchAsync() {
    await using var conn = await _openAsync();
    var id = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New();
    await _executeAsync(conn, """
      INSERT INTO wh_event_metadata_staging (event_id, recorded_at, route_id, event_type, metadata)
      VALUES (@id, now(), @rid, 'X', '{"Hops":[{"ts":"yesterday","co":"not-a-uuid","ca":"","du":"forever"}, 7]}')
      """, ("id", id), ("rid", (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New()));

    var result = await _distillAsync(conn, 100);

    await Assert.That(result.Consumed).IsEqualTo(1L);
    await Assert.That(await _scalarAsync<bool>(conn,
      "SELECT occurred_at IS NULL AND correlation_id IS NULL AND causation_id IS NULL FROM wh_traces WHERE event_id = @e", ("e", id))).IsTrue();
    await Assert.That(await _scalarAsync<long>(conn, "SELECT duration_count FROM wh_routes")).IsEqualTo(0L);
    await Assert.That(await _scalarAsync<double?>(conn, "SELECT _wh_timespan_ms('-1.02:03:04.5')")).IsEqualTo(-(86400000d + (2 * 3600000) + (3 * 60000) + 4500));
    await Assert.That(await _scalarAsync<Guid?>(conn, "SELECT _wh_try_uuid('01900000-0000-7000-8000-0000000000c2')"))
      .IsEqualTo(StoredMetadataCorpus.Correlation.Value).Because("control: a well-formed value still parses");
  }

  // ===== Reconstruction =====

  [Test]
  public async Task Reconstruct_WithinTheTraceWindow_IsLosslessAsync() {
    await using var conn = await _openAsync();
    foreach (var entry in StoredMetadataCorpus.Entries) {
      var id = await _appendAsync(conn, entry.EventType, entry.Hops, extra: ""","dc":{"m":2,"s":1},"rin":[]""");
      var staged = await _scalarAsync<string>(conn, "SELECT metadata::text FROM wh_event_metadata_staging WHERE event_id = @e", ("e", id));
      await Assert.That(await _sourceAsync(conn, id)).IsEqualTo("staged").Because("control: before distillation the staged form answers");

      await _distillAsync(conn, 100);

      await Assert.That(await _sourceAsync(conn, id)).IsEqualTo("trace");
      await Assert.That(await _scalarAsync<bool>(conn, "SELECT metadata = @staged::jsonb FROM wh_trace_metadata(ARRAY[@e]::uuid[])",
        ("e", id), ("staged", staged))).IsTrue()
        .Because($"corpus entry '{entry.Name}': the row, its trace and its route rebuild exactly the metadata that was written");
    }
  }

  [Test]
  public async Task Reconstruct_AfterTheTraceAgesOut_KeepsTheRouteAsync() {
    await using var conn = await _openAsync();
    var hops = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };
    var id = await _appendAsync(conn, ORDER_PLACED, hops);
    await _distillAsync(conn, 100);
    await _executeAsync(conn, "DELETE FROM wh_traces WHERE event_id = @e", ("e", id));

    await Assert.That(await _sourceAsync(conn, id)).IsEqualTo("row");
    await Assert.That(await _scalarAsync<Guid>(conn, "SELECT route_id FROM wh_trace_metadata(ARRAY[@e]::uuid[])", ("e", id)))
      .IsEqualTo(RouteNormalizer.RouteId(ORDER_PLACED, hops)).Because("the row's route id still says how the event got here");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT NOT (metadata ? 'rid') AND jsonb_array_length(metadata -> 'Hops') = 1 FROM wh_trace_metadata(ARRAY[@e]::uuid[])", ("e", id)))
      .IsTrue().Because("past the trace window the row alone answers, in its slim form");
  }

  // ===== Retention =====

  [Test]
  public async Task Retention_EachWindow_RemovesOnlyWhatIsPastItAsync() {
    await using var conn = await _openAsync();
    await _executeAsync(conn, """
      INSERT INTO wh_event_metadata_staging (event_id, recorded_at, route_id, event_type, metadata) VALUES
        (gen_random_uuid(), now() - interval '2 days' - interval '1 minute', gen_random_uuid(), 'old', '{}'),
        (gen_random_uuid(), now() - interval '2 days' + interval '1 minute', gen_random_uuid(), 'young', '{}');
      INSERT INTO wh_traces (event_id, recorded_at, route_id, hops) VALUES
        (gen_random_uuid(), now() - interval '30 days' - interval '1 minute', gen_random_uuid(), '[]'),
        (gen_random_uuid(), now() - interval '30 days' + interval '1 minute', gen_random_uuid(), '[]');
      INSERT INTO wh_routes (route_id, event_type, hops, first_seen, last_seen) VALUES
        (gen_random_uuid(), 'old', '[]', now() - interval '400 days', now() - interval '365 days' - interval '1 minute'),
        (gen_random_uuid(), 'young', '[]', now() - interval '400 days', now() - interval '365 days' + interval '1 minute');
      """);

    var tasks = await _reapAsync(conn);

    await Assert.That(tasks["purge_old_staged_metadata"]).IsEqualTo(1L);
    await Assert.That(tasks["purge_old_traces"]).IsEqualTo(1L);
    await Assert.That(tasks["purge_idle_routes"]).IsEqualTo(1L);
    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_event_metadata_staging")).IsEqualTo(1L).Because("a staged row just inside two days stays");
    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_traces")).IsEqualTo(1L).Because("a trace just inside thirty days stays");
    await Assert.That(await _scalarAsync<string>(conn, "SELECT event_type FROM wh_routes")).IsEqualTo("young")
      .Because("a route taken just inside a year stays, however long ago it was first seen");

    await _settingAsync(conn, "trace_retention_days", "0");
    await Assert.That((await _reapAsync(conn))["purge_old_traces"]).IsEqualTo(1L).Because("the window is read from wh_settings");
  }

  [Test]
  public async Task Retention_DebugMode_RemovesNothingAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, "debug_mode", "true");
    await _executeAsync(conn, """
      INSERT INTO wh_event_metadata_staging (event_id, recorded_at, route_id, event_type, metadata)
        VALUES (gen_random_uuid(), now() - interval '9 days', gen_random_uuid(), 'old', '{}');
      INSERT INTO wh_traces (event_id, recorded_at, route_id, hops) VALUES (gen_random_uuid(), now() - interval '90 days', gen_random_uuid(), '[]');
      INSERT INTO wh_routes (route_id, event_type, hops, first_seen, last_seen)
        VALUES (gen_random_uuid(), 'old', '[]', now() - interval '900 days', now() - interval '900 days');
      """);

    var statuses = await _reapStatusesAsync(conn);

    await Assert.That(statuses["purge_old_staged_metadata"]).IsEqualTo("skipped (debug_mode=true)");
    await Assert.That(statuses["purge_old_traces"]).IsEqualTo("skipped (debug_mode=true)");
    await Assert.That(statuses["purge_idle_routes"]).IsEqualTo("skipped (debug_mode=true)");
    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_event_metadata_staging") + await _countAsync(conn, "SELECT count(*) FROM wh_traces") + await _countAsync(conn, "SELECT count(*) FROM wh_routes"))
      .IsEqualTo(3L);
  }

  // ===== The maintenance step =====

  [Test]
  public async Task Step_DistillsInBatches_UntilTheStagingIsEmptyAsync() {
    await using var conn = await _openAsync();
    for (var i = 0; i < 5; i++) {
      await _appendAsync(conn, ORDER_PLACED, [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]);
    }
    var logger = new ListLogger();
    var step = new MetadataDistillationStep(logger, batchSize: 2, maxBatches: 10);

    var result = await step.DistillAsync(_services(schema: "public"), CancellationToken.None);

    await Assert.That(result.Batches).IsEqualTo(3).Because("two full batches, then a short one that ends the cycle");
    await Assert.That(result.StagedConsumed).IsEqualTo(5L);
    await Assert.That(result.RoutesAdded).IsEqualTo(1L);
    await Assert.That(result.RoutesUpdated).IsEqualTo(2L);
    await Assert.That(result.TracesWritten).IsEqualTo(5L);
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains("Distilled 5", StringComparison.Ordinal))).IsTrue();
    await Assert.That(step.Name).IsEqualTo("metadata-distillation");
  }

  [Test]
  public async Task Step_StopsAtTheCycleLimit_AndTheNextCycleGoesOnAsync() {
    await using var conn = await _openAsync();
    for (var i = 0; i < 5; i++) {
      await _appendAsync(conn, ORDER_PLACED, [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]);
    }
    var step = new MetadataDistillationStep(NullLogger.Instance, batchSize: 2, maxBatches: 1);

    var first = await step.DistillAsync(_services(), CancellationToken.None);
    await step.RunAsync(_services(), CancellationToken.None);

    await Assert.That(first.StagedConsumed).IsEqualTo(2L).Because("one batch is the cycle's limit");
    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_event_metadata_staging")).IsEqualTo(1L).Because("the next cycle took the next batch");
  }

  [Test]
  public async Task Step_UnderAnotherForm_CallsOnce_AndLogsTheSkipAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, "metadata_write_form", "Slim");
    var logger = new ListLogger();

    var result = await new MetadataDistillationStep(logger, batchSize: 2, maxBatches: 10).DistillAsync(_services(), CancellationToken.None);

    await Assert.That(result.Batches).IsEqualTo(1);
    await Assert.That(result.Status).IsEqualTo("skipped (metadata_write_form=Slim)");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Debug && e.Message.Contains("metadata_write_form=Slim", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task FromServices_WithOrWithoutLogging_BuildsTheStepAsync() {
    var withLogging = new ServiceCollection().AddLogging().BuildServiceProvider();

    await Assert.That(MetadataDistillationStep.FromServices(withLogging)).IsNotNull();
    await Assert.That(MetadataDistillationStep.FromServices(new ServiceCollection().BuildServiceProvider())).IsNotNull();
    await Assert.That(() => MetadataDistillationStep.FromServices(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new MetadataDistillationStep(null!, 1, 1)).Throws<ArgumentNullException>();
    await Assert.That(async () => await MetadataDistillationStep.FromServices(withLogging).DistillAsync(null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  // ===== Helpers =====

  private sealed class ListLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }

  private ServiceProvider _services(string? schema = null) =>
    new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, schema))
      .BuildServiceProvider();

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  /// <summary>Appends an event through wh_append_event, which stages its full metadata under the default form.</summary>
  private static async Task<Guid> _appendAsync(NpgsqlConnection conn, string eventType, List<MessageHop> hops, string extra = "") {
    var id = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New();
    await _executeAsync(conn, """
      SELECT wh_append_event(@id, @sid, @sid, split_part(@type, ',', 1), @type, '{"amount": 1}'::jsonb, @meta::jsonb, NULL, now(), 0)
      """,
      ("id", id), ("sid", (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New()), ("type", eventType),
      ("meta", $$"""{"MessageId":"{{id}}","Hops":{{StoredMetadataCorpus.HopsJson(hops)}}{{extra}}}"""));
    return id;
  }

  private sealed record Distilled(long Consumed, long RoutesAdded, long RoutesUpdated, long Traces, string Status);

  private static async Task<Distilled> _distillAsync(NpgsqlConnection conn, int batch) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM wh_distill_event_metadata(@batch)";
    cmd.Parameters.AddWithValue(nameof(batch), batch);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return new Distilled(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4));
  }

  private static async Task<(long Inserted, long Updated)> _routeWritesAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT n_tup_ins, n_tup_upd FROM pg_stat_xact_user_tables WHERE relname = 'wh_routes'";
    await using var reader = await cmd.ExecuteReaderAsync();
    return await reader.ReadAsync() ? (reader.GetInt64(0), reader.GetInt64(1)) : (0, 0);
  }

  private static Task<string> _sourceAsync(NpgsqlConnection conn, Guid id) =>
    _scalarAsync<string>(conn, "SELECT source FROM wh_trace_metadata(ARRAY[@e]::uuid[])", ("e", id));

  private static async Task<Dictionary<string, long>> _reapAsync(NpgsqlConnection conn) {
    var rows = new Dictionary<string, long>(StringComparer.Ordinal);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT task_name, rows_affected FROM reap_unbounded_ledgers()";
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows[reader.GetString(0)] = reader.GetInt64(1);
    }
    return rows;
  }

  private static async Task<Dictionary<string, string>> _reapStatusesAsync(NpgsqlConnection conn) {
    var rows = new Dictionary<string, string>(StringComparer.Ordinal);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT task_name, status FROM reap_unbounded_ledgers()";
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows[reader.GetString(0)] = reader.GetString(1);
    }
    return rows;
  }

  private static Task<long> _countAsync(NpgsqlConnection conn, string countSql) => _scalarAsync<long>(conn, countSql);

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    var result = await cmd.ExecuteScalarAsync();
    return result is DBNull or null ? default! : (T)result;
  }

  private static Task _settingAsync(NpgsqlConnection conn, string key, string value) =>
    _executeAsync(conn, """
      INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES (@k, @v, 'string')
      ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value
      """, ("k", key), ("v", value));

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }
}
