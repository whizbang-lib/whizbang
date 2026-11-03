// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// History conversion (#1727, migration 298): rows written with full metadata before slim stored metadata (285) are
/// rewritten once, in bounded batches from a cursor, to the slim form plus their route id; under <c>Linked</c> their
/// routes are counted and the ones still inside the trace window get a trace, so reconstruction stays lossless. Rows
/// that already carry a route id, rows of types kept full and rows it cannot read are never touched; debug mode, the
/// <c>Full</c> form and <c>ConvertHistory = false</c> skip the task.
/// </summary>
/// <docs>data/event-store#history-conversion</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/298_ConvertHistoricalMetadata.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/MetadataHistoryConversionStep.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class MetadataHistoryConversionSqlTests : EFCoreTestBase {
  private const string ORDER_PLACED = "MyApp.Orders.OrderPlaced, MyApp";
  private const string PAYMENT_CAPTURED = "MyApp.Payments.PaymentCaptured, MyApp";

  // ===== What is converted =====

  [Test]
  public async Task Convert_ARowWithoutARouteId_BecomesSlimWithItsRouteId_AndARowWithOneIsUntouchedAsync() {
    await using var conn = await _openAsync();
    var hops = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };
    var historic = await _historicAsync(conn, ORDER_PLACED, hops);
    var current = await _appendAsync(conn, ORDER_PLACED, hops);
    var currentBefore = await _rowAsync(conn, current);

    var result = await _convertAsync(conn, 100);

    await Assert.That(result.Affected).IsEqualTo(1L).Because("only the row without a route id is rewritten");
    await Assert.That(result.Scanned).IsEqualTo(2L);
    await Assert.That(await _scalarAsync<string>(conn, "SELECT metadata ->> 'rid' FROM wh_event_body WHERE event_id = @e", ("e", historic)))
      .IsEqualTo(RouteNormalizer.RouteId(ORDER_PLACED, hops).ToString()).Because("the route id is the one the write paths stamp");
    await Assert.That(await _scalarAsync<bool>(conn,
      "SELECT jsonb_array_length(metadata -> 'Hops') = 1 AND NOT (metadata -> 'Hops' -> 0 ? 'si') FROM wh_event_body WHERE event_id = @e", ("e", historic)))
      .IsTrue().Because("the hop list is reduced to the slim hop");
    await Assert.That(await _scalarAsync<bool>(conn,
      "SELECT b.metadata = _wh_slim_event_metadata(@full::jsonb, _wh_route_id('MyApp.Orders.OrderPlaced', @full::jsonb -> 'Hops')) FROM wh_event_body b WHERE b.event_id = @e",
      ("e", historic), ("full", _fullJson(historic, hops)))).IsTrue().Because("the converted row is exactly what the write paths store for it now");
    await Assert.That(await _rowAsync(conn, current)).IsEqualTo(currentBefore)
      .Because("a row written since slim metadata carries its route id, and is never rewritten (same tuple, same bytes)");
  }

  [Test]
  public async Task Convert_AConvertedRow_IsNotTouchedAgain_EvenFromTheStartAsync() {
    await using var conn = await _openAsync();
    var id = await _historicAsync(conn, ORDER_PLACED, [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]);
    var first = await _convertAsync(conn, 100);
    var converted = await _rowAsync(conn, id);

    await _executeAsync(conn, "DELETE FROM wh_metadata_history_conversion");
    var again = await _convertAsync(conn, 100);

    await Assert.That(first.Affected).IsEqualTo(1L).Because("control: the row was converted the first time");
    await Assert.That(again.Scanned).IsEqualTo(1L).Because("a reset cursor reads the table from the start again");
    await Assert.That(again.Affected).IsEqualTo(0L).Because("a row with a route id is never rewritten, whatever the cursor says");
    await Assert.That(await _rowAsync(conn, id)).IsEqualTo(converted);
  }

  [Test]
  public async Task Convert_ARowOfATypeKeptFull_IsSkipped_AndCountedAsKeptAsync() {
    await using var conn = await _openAsync();
    var hops = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Billing) };
    var kept = await _historicAsync(conn, PAYMENT_CAPTURED, hops);
    var other = await _historicAsync(conn, ORDER_PLACED, hops);
    var keptBefore = await _rowAsync(conn, kept);
    await _settingAsync(conn, "metadata_full_for", "MyApp.Payments");

    var result = await _convertAsync(conn, 100);

    await Assert.That(result.KeptFull).IsEqualTo(1L);
    await Assert.That(result.Affected).IsEqualTo(1L).Because("control: the type that is not listed is converted");
    await Assert.That(await _rowAsync(conn, kept)).IsEqualTo(keptBefore).Because("a listed namespace keeps its rows full, untouched");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT metadata ? 'rid' FROM wh_event_body WHERE event_id = @e", ("e", other))).IsTrue();
  }

  [Test]
  public async Task Convert_RowsItCannotRead_AreLeftAlone_AndNeverStopTheBatchAsync() {
    await using var conn = await _openAsync();
    var array = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New();
    var orphan = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New();
    var historic = await _historicAsync(conn, ORDER_PLACED, [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]);
    await _executeAsync(conn, """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
        VALUES (@a, @a, @a, 'X', 'X', 1, now());
      INSERT INTO wh_event_body (event_id, event_data, metadata) VALUES (@a, '{}', '[1, 2]'), (@o, '{}', '{"MessageId":"x"}');
      """, ("a", array), ("o", orphan));

    var result = await _convertAsync(conn, 100);

    await Assert.That(result.Scanned).IsEqualTo(3L);
    await Assert.That(result.Affected).IsEqualTo(1L).Because("control: the readable row in the same batch is converted");
    await Assert.That(await _scalarAsync<string>(conn, "SELECT metadata::text FROM wh_event_body WHERE event_id = @e", ("e", array))).IsEqualTo("[1, 2]")
      .Because("metadata that is not an object has nothing to slim");
    await Assert.That(await _scalarAsync<string>(conn, "SELECT metadata::text FROM wh_event_body WHERE event_id = @e", ("e", orphan))).IsEqualTo("{\"MessageId\": \"x\"}")
      .Because("a body without its pointer has no type, so no route id can be computed for it");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT metadata ? 'rid' FROM wh_event_body WHERE event_id = @e", ("e", historic))).IsTrue();
  }

  // ===== When it runs =====

  [Test]
  public async Task Convert_UnderDebugMode_SkipsTheTask_AndRunsOnceItIsOffAsync() {
    await using var conn = await _openAsync();
    var id = await _historicAsync(conn, ORDER_PLACED, [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]);
    var before = await _rowAsync(conn, id);
    await _settingAsync(conn, "debug_mode", "true");

    var skipped = await _convertAsync(conn, 100);
    var rowWhileSkipped = await _rowAsync(conn, id);
    await _settingAsync(conn, "debug_mode", "false");
    var ran = await _convertAsync(conn, 100);

    await Assert.That(skipped.Status).IsEqualTo("skipped (debug_mode=true)");
    await Assert.That(skipped.Scanned).IsEqualTo(0L);
    await Assert.That(rowWhileSkipped).IsEqualTo(before).Because("debug mode keeps every row full, history included");
    await Assert.That(ran.Affected).IsEqualTo(1L).Because("control: with debug mode off the same row is converted");
  }

  [Test]
  [Arguments("metadata_write_form", "Full", "skipped (metadata_write_form=Full)")]
  [Arguments("metadata_convert_history", "false", "skipped (metadata_convert_history=false)")]
  [Arguments("metadata_convert_history", " OFF ", "skipped (metadata_convert_history=false)")]
  [Arguments("metadata_convert_history", "0", "skipped (metadata_convert_history=false)")]
  public async Task Convert_UnderTheFullFormOrSwitchedOff_SkipsTheTaskAsync(string key, string value, string status) {
    await using var conn = await _openAsync();
    var id = await _historicAsync(conn, ORDER_PLACED, [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]);
    var before = await _rowAsync(conn, id);
    await _settingAsync(conn, key, value);

    var result = await _convertAsync(conn, 100);

    await Assert.That(result.Status).IsEqualTo(status);
    await Assert.That(result.Affected).IsEqualTo(0L);
    await Assert.That(await _rowAsync(conn, id)).IsEqualTo(before);
  }

  [Test]
  [Arguments("true")]
  [Arguments("yes")]
  [Arguments("not a boolean")]
  public async Task Convert_AnyOtherConvertHistoryValue_RunsTheTaskAsync(string value) {
    await using var conn = await _openAsync();
    await _historicAsync(conn, ORDER_PLACED, [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]);
    await _settingAsync(conn, "metadata_convert_history", value);

    var result = await _convertAsync(conn, 100);

    await Assert.That(result.Affected).IsEqualTo(1L).Because("only an explicit off turns the conversion off; a missing or mistyped value is the default, on");
  }

  // ===== Routes and traces =====

  [Test]
  public async Task Convert_UnderLinked_CountsTheRoute_AndTracesOnlyWhatIsInsideTheTraceWindowAsync() {
    await using var conn = await _openAsync();
    var hops = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };
    var inside = await _historicAsync(conn, ORDER_PLACED, hops, age: "30 days - 1 minute", extra: ""","dc":{"m":2,"s":1},"rin":[]""");
    var outside = await _historicAsync(conn, ORDER_PLACED, hops, age: "30 days + 1 minute");
    var original = await _scalarAsync<string>(conn, "SELECT metadata::text FROM wh_event_body WHERE event_id = @e", ("e", inside));
    var routeId = RouteNormalizer.RouteId(ORDER_PLACED, hops);

    var result = await _convertAsync(conn, 100);

    await Assert.That(result.RoutesAdded).IsEqualTo(1L);
    await Assert.That(result.Traces).IsEqualTo(1L).Because("an event older than the trace window would be reaped at once, so it gets no trace");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT occurrences FROM wh_routes WHERE route_id = @r", ("r", routeId))).IsEqualTo(2L)
      .Because("every converted event is an occurrence of its route, inside the window or not");
    await Assert.That(await _scalarAsync<bool>(conn,
      "SELECT r.first_seen = (SELECT created_at FROM wh_event_store WHERE event_id = @o) AND r.last_seen = (SELECT created_at FROM wh_event_store WHERE event_id = @i) FROM wh_routes r WHERE r.route_id = @r",
      ("r", routeId), ("o", outside), ("i", inside))).IsTrue().Because("a route is first and last seen when its events were stored, not when they were converted");
    await Assert.That(await _scalarAsync<bool>(conn,
      "SELECT recorded_at = (SELECT created_at FROM wh_event_store WHERE event_id = @e) FROM wh_traces WHERE event_id = @e", ("e", inside)))
      .IsTrue().Because("a trace is kept for the window from its event's time, as a distilled trace is");
    await Assert.That(await _scalarAsync<string>(conn, "SELECT source FROM wh_trace_metadata(ARRAY[@e]::uuid[])", ("e", inside))).IsEqualTo("trace");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT metadata = @original::jsonb FROM wh_trace_metadata(ARRAY[@e]::uuid[])",
      ("e", inside), ("original", original))).IsTrue()
      .Because("the converted row, its trace and its route rebuild exactly the metadata the row held before");
    await Assert.That(await _scalarAsync<string>(conn, "SELECT source FROM wh_trace_metadata(ARRAY[@e]::uuid[])", ("e", outside))).IsEqualTo("row");
  }

  [Test]
  public async Task Convert_AnExistingRoute_OnlyCountsAsync() {
    await using var conn = await _openAsync();
    var hops = new List<MessageHop> { StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders) };
    await _appendAsync(conn, ORDER_PLACED, hops);
    await _executeAsync(conn, "SELECT * FROM wh_distill_event_metadata(100)");
    await _historicAsync(conn, ORDER_PLACED, hops, age: "2 days");

    var result = await _convertAsync(conn, 100);

    await Assert.That(result.RoutesAdded).IsEqualTo(0L);
    await Assert.That(result.RoutesUpdated).IsEqualTo(1L);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT occurrences FROM wh_routes")).IsEqualTo(2L);
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT first_seen < last_seen FROM wh_routes")).IsTrue()
      .Because("an older converted event moves the route's first seen back and leaves its last seen");
  }

  [Test]
  public async Task Convert_UnderSlim_WritesNoRoutesOrTracesAsync() {
    await using var conn = await _openAsync();
    var id = await _historicAsync(conn, ORDER_PLACED, [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]);
    await _settingAsync(conn, "metadata_write_form", "Slim");

    var result = await _convertAsync(conn, 100);

    await Assert.That(result.Affected).IsEqualTo(1L).Because("Slim stores the slim row too");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_routes") + await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_traces"))
      .IsEqualTo(0L).Because("Slim keeps no routes or traces for new events, so none for old ones either");
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT metadata ? 'rid' FROM wh_event_body WHERE event_id = @e", ("e", id))).IsTrue();
  }

  // ===== Batches, the cursor and other transactions =====

  [Test]
  public async Task Convert_TakesAtMostTheBatch_ResumesFromItsCursor_AndCompletesAsync() {
    await using var conn = await _openAsync();
    var ids = new List<Guid>();
    for (var i = 0; i < 3; i++) {
      ids.Add(await _historicAsync(conn, ORDER_PLACED, [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]));
    }

    var first = await _convertAsync(conn, 2);
    var lastBefore = await _scalarAsync<bool>(conn, "SELECT metadata ? 'rid' FROM wh_event_body WHERE event_id = @e", ("e", ids.Max()));
    var second = await _convertAsync(conn, 2);
    var third = await _convertAsync(conn, 2);

    await Assert.That(first.Scanned).IsEqualTo(2L);
    await Assert.That(first.Affected).IsEqualTo(2L);
    await Assert.That(lastBefore).IsFalse().Because("a batch reads at most its size, oldest event id first");
    await Assert.That(second.Scanned).IsEqualTo(1L).Because("the next batch starts after the last row the cursor passed");
    await Assert.That(second.Affected).IsEqualTo(1L);
    await Assert.That(second.Status).IsEqualTo("ok");
    await Assert.That(third.Status).IsEqualTo("complete").Because("a short batch reached the end of the table");
    await Assert.That(third.Scanned).IsEqualTo(0L).Because("once complete, a cycle reads nothing");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT rows_converted FROM wh_metadata_history_conversion")).IsEqualTo(3L);
    await Assert.That(await _scalarAsync<bool>(conn, "SELECT completed_at IS NOT NULL AND last_event_id = @e FROM wh_metadata_history_conversion", ("e", ids.Max())))
      .IsTrue();
  }

  [Test]
  public async Task Convert_ARowAnotherTransactionHolds_IsLeftForTheNextBatch_WithoutWaitingAsync() {
    await using var conn = await _openAsync();
    var ids = new List<Guid>();
    for (var i = 0; i < 3; i++) {
      ids.Add(await _historicAsync(conn, ORDER_PLACED, [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]));
    }
    ids.Sort();
    await using var holder = await _openAsync();
    await using var hold = await holder.BeginTransactionAsync();
    await _executeAsync(holder, "SELECT 1 FROM wh_event_body WHERE event_id = @e FOR UPDATE", ("e", ids[1]));
    // A wait would raise here instead of hanging the test.
    await _executeAsync(conn, "SET lock_timeout = '5s'");

    var held = await _convertAsync(conn, 100);
    var cursor = await _scalarAsync<Guid>(conn, "SELECT last_event_id FROM wh_metadata_history_conversion");
    await hold.RollbackAsync();
    var next = await _convertAsync(conn, 100);
    var after = await _convertAsync(conn, 100);

    await Assert.That(held.Status).IsEqualTo("held (1 locked row(s) left for the next batch)");
    await Assert.That(held.Affected).IsEqualTo(2L).Because("the rows either side of the held one are converted without waiting for it");
    await Assert.That(cursor).IsEqualTo(ids[0]).Because("the cursor stops before the held row, so the next batch reads it again");
    await Assert.That(next.Affected).IsEqualTo(1L).Because("released, the held row is converted; the two after it already carry their route id");
    await Assert.That(next.Status).IsEqualTo("ok");
    await Assert.That(after.Status).IsEqualTo("complete");
  }

  [Test]
  public async Task Convert_WhileAnotherInstanceConverts_SkipsWithoutWaitingAsync() {
    await using var conn = await _openAsync();
    await _historicAsync(conn, ORDER_PLACED, [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]);
    await using var holder = await _openAsync();
    await using var hold = await holder.BeginTransactionAsync();
    await _executeAsync(holder, "SELECT 1 FROM wh_metadata_history_conversion FOR UPDATE");
    await _executeAsync(conn, "SET lock_timeout = '5s'");

    var skipped = await _convertAsync(conn, 100);
    await hold.RollbackAsync();
    var ran = await _convertAsync(conn, 100);

    await Assert.That(skipped.Status).IsEqualTo("skipped (another instance is converting)");
    await Assert.That(skipped.Affected).IsEqualTo(0L);
    await Assert.That(ran.Affected).IsEqualTo(1L).Because("control: once the other instance is done, this one converts");
  }

  // ===== The maintenance step =====

  [Test]
  public async Task Step_ConvertsInBatches_UntilTheTableIsCompleteAsync() {
    await using var conn = await _openAsync();
    for (var i = 0; i < 5; i++) {
      await _historicAsync(conn, ORDER_PLACED, [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]);
    }
    var logger = new ListLogger();
    var step = new MetadataHistoryConversionStep(logger, batchSize: 2, maxBatches: 10);

    var result = await step.ConvertAsync(_services(schema: "public"), CancellationToken.None);

    await Assert.That(result.Batches).IsEqualTo(3).Because("two full batches, then a short one that completes the table");
    await Assert.That(result.RowsScanned).IsEqualTo(5L);
    await Assert.That(result.RowsAffected).IsEqualTo(5L);
    await Assert.That(result.RowsKeptFull).IsEqualTo(0L);
    await Assert.That(result.RoutesAdded).IsEqualTo(1L);
    await Assert.That(result.RoutesUpdated).IsEqualTo(2L);
    await Assert.That(result.TracesWritten).IsEqualTo(5L);
    await Assert.That(result.Status).IsEqualTo("ok");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains("Converted 5", StringComparison.Ordinal))).IsTrue();
    await Assert.That(step.Name).IsEqualTo("metadata-history-conversion");
  }

  [Test]
  public async Task Step_StopsAtTheCycleLimit_AndTheNextCycleGoesOnAsync() {
    await using var conn = await _openAsync();
    for (var i = 0; i < 5; i++) {
      await _historicAsync(conn, ORDER_PLACED, [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]);
    }
    var step = new MetadataHistoryConversionStep(NullLogger.Instance, batchSize: 2, maxBatches: 1);

    var first = await step.ConvertAsync(_services(), CancellationToken.None);
    await step.RunAsync(_services(), CancellationToken.None);

    await Assert.That(first.RowsAffected).IsEqualTo(2L).Because("one batch is the cycle's limit");
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_event_body WHERE NOT (metadata ? 'rid')")).IsEqualTo(1L)
      .Because("the next cycle took the next batch");
  }

  [Test]
  public async Task Step_WhenSkippedOrComplete_CallsOnce_AndLogsItAsync() {
    await using var conn = await _openAsync();
    await _settingAsync(conn, "metadata_convert_history", "false");
    var logger = new ListLogger();
    var step = new MetadataHistoryConversionStep(logger, batchSize: 2, maxBatches: 10);

    var skipped = await step.ConvertAsync(_services(), CancellationToken.None);
    await _settingAsync(conn, "metadata_convert_history", "true");
    await step.ConvertAsync(_services(), CancellationToken.None);
    var complete = await step.ConvertAsync(_services(), CancellationToken.None);

    await Assert.That(skipped.Batches).IsEqualTo(1);
    await Assert.That(skipped.Status).IsEqualTo("skipped (metadata_convert_history=false)");
    await Assert.That(complete.Batches).IsEqualTo(1);
    await Assert.That(complete.Status).IsEqualTo("complete");
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Debug && e.Message.Contains("metadata_convert_history=false", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task Step_AHeldBatch_EndsTheCycleAsync() {
    await using var conn = await _openAsync();
    var ids = new List<Guid>();
    for (var i = 0; i < 3; i++) {
      ids.Add(await _historicAsync(conn, ORDER_PLACED, [StoredMetadataCorpus.RichHop(StoredMetadataCorpus.Orders)]));
    }
    await using var holder = await _openAsync();
    await using var hold = await holder.BeginTransactionAsync();
    await _executeAsync(holder, "SELECT 1 FROM wh_event_body WHERE event_id = @e FOR UPDATE", ("e", ids.Min()));

    var result = await new MetadataHistoryConversionStep(NullLogger.Instance, batchSize: 2, maxBatches: 10).ConvertAsync(_services(), CancellationToken.None);
    await hold.RollbackAsync();

    await Assert.That(result.Batches).IsEqualTo(1).Because("a batch that could not move its cursor would only read the same rows again this cycle");
    await Assert.That(result.Status).StartsWith("held");
  }

  [Test]
  public async Task FromServices_WithOrWithoutLogging_BuildsTheStepAsync() {
    var withLogging = new ServiceCollection().AddLogging().BuildServiceProvider();

    await Assert.That(MetadataHistoryConversionStep.FromServices(withLogging)).IsNotNull();
    await Assert.That(MetadataHistoryConversionStep.FromServices(new ServiceCollection().BuildServiceProvider())).IsNotNull();
    await Assert.That(() => MetadataHistoryConversionStep.FromServices(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new MetadataHistoryConversionStep(null!, 1, 1)).Throws<ArgumentNullException>();
    await Assert.That(async () => await MetadataHistoryConversionStep.FromServices(withLogging).ConvertAsync(null!, CancellationToken.None))
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

  private static string _fullJson(Guid id, List<MessageHop> hops, string extra = "") =>
    $$"""{"MessageId":"{{id}}","Hops":{{StoredMetadataCorpus.HopsJson(hops)}}{{extra}}}""";

  /// <summary>Appends an event through wh_append_event under the default form: a row written since slim metadata.</summary>
  private static async Task<Guid> _appendAsync(NpgsqlConnection conn, string eventType, List<MessageHop> hops, string age = "0 seconds", string extra = "") {
    var id = (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New();
    await _executeAsync(conn, """
      SELECT wh_append_event(@id, @sid, @sid, split_part(@type, ',', 1), @type, '{"amount": 1}'::jsonb, @meta::jsonb, NULL,
                             now() - @age::interval, 0)
      """,
      ("id", id), ("sid", (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New()), ("type", eventType), ("age", age),
      ("meta", _fullJson(id, hops, extra)));
    return id;
  }

  /// <summary>
  /// A row as it was written before slim metadata: appended under the Full form (the row keeps every hop and stages
  /// nothing), then without the route id 285 added, which is what a row written before 285 never had.
  /// </summary>
  private static async Task<Guid> _historicAsync(NpgsqlConnection conn, string eventType, List<MessageHop> hops, string age = "0 seconds", string extra = "") {
    await _settingAsync(conn, "metadata_write_form", "Full");
    var id = await _appendAsync(conn, eventType, hops, age, extra);
    await _executeAsync(conn, "DELETE FROM wh_settings WHERE setting_key = 'metadata_write_form'");
    await _executeAsync(conn, "UPDATE wh_event_body SET metadata = metadata - 'rid' WHERE event_id = @e", ("e", id));
    return id;
  }

  /// <summary>The row's tuple id, transaction and bytes: all three are unchanged only if the row was never rewritten.</summary>
  private static Task<string> _rowAsync(NpgsqlConnection conn, Guid id) =>
    _scalarAsync<string>(conn, "SELECT ctid::text || '|' || xmin::text || '|' || metadata::text FROM wh_event_body WHERE event_id = @e", ("e", id));

  private sealed record Converted(long Scanned, long Affected, long KeptFull, long RoutesAdded, long RoutesUpdated, long Traces, string Status);

  private static async Task<Converted> _convertAsync(NpgsqlConnection conn, int batch) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT rows_scanned, rows_affected, rows_kept_full, routes_added, routes_updated, traces_written, status FROM wh_convert_event_metadata_history(@batch)";
    cmd.Parameters.AddWithValue(nameof(batch), batch);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return new Converted(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetString(6));
  }

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
