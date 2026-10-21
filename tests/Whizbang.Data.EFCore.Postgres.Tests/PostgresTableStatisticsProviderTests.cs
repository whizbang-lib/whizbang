// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres.Observability;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresTableStatisticsProvider"/> against real PostgreSQL.
/// Verifies pg_stat_user_tables and queue depth queries return correct data.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Observability/PostgresTableStatisticsProvider.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class PostgresTableStatisticsProviderTests : EFCoreTestBase {
  private NpgsqlDataSource _dataSource = null!;
  private PostgresTableStatisticsProvider _provider = null!;

  [Before(Test)]
  public async Task TestSetupAsync() {
    _dataSource = NpgsqlDataSource.Create(ConnectionString);
    _provider = new PostgresTableStatisticsProvider(_dataSource);
    await Task.CompletedTask;
  }

  [After(Test)]
  public async Task TestTeardownAsync() => await _dataSource.DisposeAsync();

  [Test]
  public async Task GetEstimatedTableSizesAsync_ReturnsAllTrackedTablesAsync() {
    var sizes = await _provider.GetEstimatedTableSizesAsync();

    // All 7 infrastructure tables should be present
    await Assert.That(sizes.ContainsKey("wh_inbox")).IsTrue();
    await Assert.That(sizes.ContainsKey("wh_outbox")).IsTrue();
    await Assert.That(sizes.ContainsKey("wh_event_store")).IsTrue();
    await Assert.That(sizes.ContainsKey("wh_active_streams")).IsTrue();
    await Assert.That(sizes.ContainsKey("wh_perspective_events")).IsTrue();
    await Assert.That(sizes.ContainsKey("wh_perspective_cursors")).IsTrue();
    await Assert.That(sizes.ContainsKey("wh_perspective_snapshots")).IsTrue();

    // All sizes should be non-negative (empty tables still have overhead)
    foreach (var (table, size) in sizes) {
      await Assert.That(size).IsGreaterThanOrEqualTo(0)
        .Because($"Table {table} should have non-negative size");
    }
  }

  // ==== #1488: every framework and perspective table, not a list of seven ====

  [Test]
  public async Task GetEstimatedTableSizesAsync_ReportsEveryFrameworkAndPerspectiveTableAsync() {
    // The event body store, the deduplication ledger and the dead letters are among the largest tables a
    // service has, and perspective tables are the rest of its data. A hard-coded list of seven reported
    // none of them, so growth in any of them was found by hand.
    var sizes = await _provider.GetEstimatedTableSizesAsync();
    var catalog = await _scalarListAsync("""
      SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
      WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND NOT c.relispartition AND c.relname LIKE 'wh\_%'
      """);

    foreach (var table in new[] { "wh_event_body", "wh_message_deduplication", "wh_dead_letters" }) {
      await Assert.That(sizes.ContainsKey(table)).IsTrue()
        .Because($"{table} is a framework table, and every framework table is reported");
    }
    await Assert.That(catalog.Any(t => t.StartsWith("wh_per_", StringComparison.Ordinal))).IsTrue()
      .Because("the test schema declares perspectives, so a perspective table must be there to find");
    await Assert.That(sizes.Keys.Order(StringComparer.Ordinal).ToList()).IsEquivalentTo(catalog.Order(StringComparer.Ordinal).ToList())
      .Because("every wh_ table in the schema is reported by name, perspective tables included, and nothing else");
  }

  [Test]
  public async Task GetTableStorageAsync_LeavesOutOtherTablesAndOtherSchemasAsync() {
    await _runAsync(
      "CREATE TABLE stats_not_framework (id int)",
      // 'wh' then a letter: a pattern that left the underscore as a wildcard would take it.
      "CREATE TABLE whatever_probe (id int)",
      "CREATE VIEW wh_view_probe AS SELECT 1 AS one",
      "CREATE SCHEMA stats_other_service",
      "CREATE TABLE stats_other_service.wh_other_service_probe (id int)");

    var storage = await _provider.GetTableStorageAsync();

    await Assert.That(storage.ContainsKey("wh_event_store")).IsTrue();
    await Assert.That(storage.ContainsKey("stats_not_framework")).IsFalse()
      .Because("only tables named like the framework's are the framework's to report");
    await Assert.That(storage.ContainsKey("whatever_probe")).IsFalse()
      .Because("the underscore in wh_ is literal, not a single-character wildcard");
    await Assert.That(storage.ContainsKey("wh_view_probe")).IsFalse()
      .Because("a view holds no storage of its own");
    await Assert.That(storage.ContainsKey("wh_other_service_probe")).IsFalse()
      .Because("another schema is another service's tables, which that service reports");
  }

  [Test]
  public async Task GetTableStorageAsync_PastTheCap_SumsTheSmallestUnderOtherAsync() {
    // Five tables of five distinct sizes, autovacuum off so no free-space or visibility map appears
    // between the two reads and moves a size under the comparison.
    var rows = new[] { 0, 200, 2_000, 8_000, 20_000 };
    var statements = new List<string> { "CREATE SCHEMA stats_cap" };
    for (var i = 0; i < rows.Length; i++) {
      statements.Add($"CREATE TABLE stats_cap.wh_cap_{i} (id int, label text) WITH (autovacuum_enabled = false)");
      statements.Add($"INSERT INTO stats_cap.wh_cap_{i} SELECT g, repeat('x', 40) FROM generate_series(1, {rows[i]}) g");
    }
    statements.Add("ANALYZE");
    await _runAsync([.. statements]);

    var all = await new PostgresTableStatisticsProvider(_dataSource, "stats_cap").GetTableStorageAsync();
    var capped = await new PostgresTableStatisticsProvider(_dataSource, "stats_cap", maxTrackedTables: 3).GetTableStorageAsync();

    var largest = all.OrderByDescending(t => t.Value.TotalBytes).Take(3).Select(t => t.Key).ToList();
    var folded = all.Keys.Except(largest).ToList();
    await Assert.That(capped.Keys.Order(StringComparer.Ordinal).ToList())
      .IsEquivalentTo(largest.Append(TableStatisticsOptions.OTHER_TABLES).Order(StringComparer.Ordinal).ToList())
      .Because("the three largest keep their names and the two smallest are summed under one series");
    await Assert.That(capped[TableStatisticsOptions.OTHER_TABLES].TotalBytes).IsEqualTo(folded.Sum(t => all[t].TotalBytes))
      .Because("a summed series that lost bytes would make every total on a dashboard wrong");
    await Assert.That(capped[TableStatisticsOptions.OTHER_TABLES].LiveRows).IsEqualTo(folded.Sum(t => all[t].LiveRows));
    await Assert.That(capped.Values.Sum(t => t.TotalBytes)).IsEqualTo(all.Values.Sum(t => t.TotalBytes))
      .Because("capping changes how many series there are, never what they add up to");
    await Assert.That(capped[TableStatisticsOptions.OTHER_TABLES].BloatRatio).IsNull()
      .Because("a ratio cannot be added, so the summed series carries none");

    var cappedSizes = await new PostgresTableStatisticsProvider(_dataSource, "stats_cap", maxTrackedTables: 3).GetEstimatedTableSizesAsync();
    await Assert.That(cappedSizes.Count).IsEqualTo(4)
      .Because("estimated_bytes is capped the same way as the split it is the total of");
  }

  [Test]
  public async Task Constructor_RefusesACapBelowOneAsync() {
    await Assert.That(() => new PostgresTableStatisticsProvider(_dataSource, "public", maxTrackedTables: 0))
      .Throws<ArgumentOutOfRangeException>()
      .Because("a cap of zero would sum every table under 'other' and name none, which no one means");
  }

  [Test]
  public async Task GetTableStorageAsync_SplitsEachTableIntoHeapToastAndIndexesAsync() {
    // Documents well past the inline limit, built from hashes so compression cannot shrink them back
    // under it: these are stored in the table's TOAST relation.
    await _runAsync(
      "CREATE SCHEMA stats_toast",
      "CREATE TABLE stats_toast.wh_toast_probe (id int PRIMARY KEY, doc jsonb) WITH (autovacuum_enabled = false, toast.autovacuum_enabled = false)",
      """
      INSERT INTO stats_toast.wh_toast_probe
      SELECT g, jsonb_build_object('blob', (SELECT string_agg(md5(g::text || ':' || i::text), '') FROM generate_series(1, 4000) i))
      FROM generate_series(1, 20) g
      """);

    var storage = await new PostgresTableStatisticsProvider(_dataSource, "stats_toast").GetTableStorageAsync();
    var probe = storage["wh_toast_probe"];
    var total = await _scalarAsync("SELECT pg_total_relation_size('stats_toast.wh_toast_probe')");

    await Assert.That(probe.ToastBytes).IsGreaterThan(probe.HeapBytes)
      .Because("large documents live in TOAST, which is the storage only this split makes visible");
    await Assert.That(probe.IndexBytes).IsGreaterThan(0L)
      .Because("the primary key is an index, and indexes are their own component");
    // Heap, TOAST heap, TOAST index and the primary key: one page of slack for each.
    await Assert.That(Math.Abs(probe.TotalBytes - total)).IsLessThanOrEqualTo(4L * 8192)
      .Because("the three components are the whole of the table's size, not an approximation of part of it");

    var sizes = await new PostgresTableStatisticsProvider(_dataSource, "stats_toast").GetEstimatedTableSizesAsync();
    await Assert.That(sizes["wh_toast_probe"]).IsEqualTo(probe.TotalBytes)
      .Because("estimated_bytes and the component split come from one reading, so they agree exactly");
  }

  [Test]
  public async Task GetTableStorageAsync_ReportsLiveRowsAsync() {
    await _runAsync(
      "CREATE SCHEMA stats_rows",
      "CREATE TABLE stats_rows.wh_rows_analyzed (id int, label text)",
      "INSERT INTO stats_rows.wh_rows_analyzed SELECT g, 'row-' || g FROM generate_series(1, 1234) g",
      "ANALYZE stats_rows.wh_rows_analyzed");

    var storage = await new PostgresTableStatisticsProvider(_dataSource, "stats_rows").GetTableStorageAsync();

    await Assert.That(storage["wh_rows_analyzed"].LiveRows).IsEqualTo(1234L)
      .Because("after ANALYZE the planner's row count is the table's row count");
  }

  [Test]
  public async Task GetTableStorageAsync_ATableNeverAnalyzed_ReportsItsCountedRowsAsync() {
    // Until a table is first analyzed or vacuumed its planner row count is -1, meaning unknown. Reporting
    // that would read as a negative row count, and reporting zero would read as empty; the statistics
    // the server keeps as rows are written answer instead. No index, because building one sets the count.
    await using (var connection = await _dataSource.OpenConnectionAsync()) {
      foreach (var sql in new[] {
          "CREATE SCHEMA stats_unanalyzed",
          "CREATE TABLE stats_unanalyzed.wh_rows_unanalyzed (id int, label text) WITH (autovacuum_enabled = false)",
          "INSERT INTO stats_unanalyzed.wh_rows_unanalyzed SELECT g, 'row-' || g FROM generate_series(1, 321) g",
          // The counts are flushed by the backend that wrote them, on its own schedule; force it.
          "SELECT pg_stat_force_next_flush()" }) {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
      }
    }

    var storage = await new PostgresTableStatisticsProvider(_dataSource, "stats_unanalyzed").GetTableStorageAsync();

    await Assert.That(storage["wh_rows_unanalyzed"].LiveRows).IsEqualTo(321L);
  }

  [Test]
  public async Task GetTableStorageAsync_APartitionedTable_IsReportedOnceWithItsPartitionsSummedAsync() {
    await _runAsync(
      "CREATE SCHEMA stats_part",
      "CREATE TABLE stats_part.wh_part_probe (id int, label text) PARTITION BY RANGE (id)",
      "CREATE TABLE stats_part.wh_part_probe_low PARTITION OF stats_part.wh_part_probe FOR VALUES FROM (0) TO (1000) WITH (autovacuum_enabled = false)",
      "CREATE TABLE stats_part.wh_part_probe_high PARTITION OF stats_part.wh_part_probe FOR VALUES FROM (1000) TO (2000) WITH (autovacuum_enabled = false)",
      "INSERT INTO stats_part.wh_part_probe SELECT g, 'row-' || g FROM generate_series(1, 1500) g",
      "ANALYZE stats_part.wh_part_probe");

    var storage = await new PostgresTableStatisticsProvider(_dataSource, "stats_part").GetTableStorageAsync();
    var partitions = await _scalarAsync(
      "SELECT pg_total_relation_size('stats_part.wh_part_probe_low') + pg_total_relation_size('stats_part.wh_part_probe_high')");

    await Assert.That(storage.Keys.ToList()).IsEquivalentTo((List<string>)["wh_part_probe"])
      .Because("a partitioned table is one table to its owner; its partitions are how it is stored");
    await Assert.That(storage["wh_part_probe"].TotalBytes).IsEqualTo(partitions)
      .Because("the parent holds no rows itself, so its size is its partitions'");
    await Assert.That(storage["wh_part_probe"].LiveRows).IsEqualTo(1500L);
  }

  [Test]
  public async Task WithoutASchema_TheConnectionsCurrentSchemaIsReadAsync() {
    // The Dapper driver's tables are wherever the connection's search path puts them.
    var provider = new PostgresTableStatisticsProvider(_dataSource, schema: null);

    var storage = await provider.GetTableStorageAsync();
    var depths = await provider.GetQueueDepthsAsync();
    var scans = await provider.GetTableScanStatisticsAsync();
    var sample = await provider.SampleEventTypeBytesAsync(new EventTypeSampleRequest("probe:search-path", 100, 50));

    await Assert.That(storage.ContainsKey("wh_event_store")).IsTrue();
    await Assert.That(depths["outbox"]).IsEqualTo(0L);
    await Assert.That(scans.ContainsKey("wh_event_store")).IsTrue()
      .Because("the scan counters behind the query-exposure advisory are read from the same schema");
    await Assert.That(sample).IsNotNull()
      .Because("the window is claimed in the same schema the events are read from");
  }

  [Test]
  public async Task GetQueueDepthsAsync_FreshDatabase_ReturnsZeroDepthsAsync() {
    var depths = await _provider.GetQueueDepthsAsync();

    await Assert.That(depths.ContainsKey("inbox")).IsTrue();
    await Assert.That(depths.ContainsKey("outbox")).IsTrue();
    await Assert.That(depths["inbox"]).IsEqualTo(0)
      .Because("Fresh database should have zero unprocessed inbox messages");
    await Assert.That(depths["outbox"]).IsEqualTo(0)
      .Because("Fresh database should have zero unprocessed outbox messages");
  }

  [Test]
  public async Task GetQueueDepthsAsync_WithUnprocessedMessages_ReturnsCorrectDepthAsync() {
    // Arrange — insert an unprocessed inbox message
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    await using var cmd = new NpgsqlCommand("""
      WITH m AS (
        INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, received_at)
        VALUES (@messageId, 'TestHandler', 'TestType', '{}'::jsonb, '{}'::jsonb, NOW())
        RETURNING message_id, stream_id, received_at, priority, is_event
      )
      INSERT INTO wh_inbox_state (message_id, stream_id, received_at, priority, is_event, status, attempts)
      SELECT message_id, stream_id, received_at, priority, is_event, 1, 0 FROM m
      """, connection);
    cmd.Parameters.AddWithValue("messageId", Guid.CreateVersion7());
    await cmd.ExecuteNonQueryAsync();

    // Act
    var depths = await _provider.GetQueueDepthsAsync();

    // Assert
    await Assert.That(depths["inbox"]).IsGreaterThanOrEqualTo(1)
      .Because("Should count the unprocessed inbox message");
  }

  // ==== #683: dead letters are the one queue whose depth was not observable ====

  [Test]
  public async Task GetQueueDepthsAsync_FreshDatabase_ReportsDeadLetterZerosAsync() {
    var depths = await _provider.GetQueueDepthsAsync();

    await Assert.That(depths.ContainsKey("dead_letters_held")).IsTrue()
      .Because("standing quarantine inventory must be a positively-reported zero, not an "
             + "absent series — a five-figure held population was invisible on dashboards "
             + "for twelve days because only transitions were counted (#683)");
    await Assert.That(depths.ContainsKey("dead_letters_pending")).IsTrue();
    await Assert.That(depths.ContainsKey("dead_letters_failed")).IsTrue();
    await Assert.That(depths["dead_letters_held"]).IsEqualTo(0);
    await Assert.That(depths["dead_letters_pending"]).IsEqualTo(0);
    await Assert.That(depths["dead_letters_failed"]).IsEqualTo(0);
  }

  [Test]
  public async Task GetQueueDepthsAsync_CountsDeadLettersByStatusAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Two held (2), one pending (0), one permanently failed (4), one recovered (3 — not
    // counted anywhere: settled rows are receipts, not depth).
    foreach (var (status, recovered) in new[] { (2, false), (2, false), (0, false), (4, false), (3, true) }) {
      await using var cmd = new NpgsqlCommand("""
        INSERT INTO wh_dead_letters
          (dead_letter_id, source_table, source_id, message_type, envelope, failure_reason,
           attempts_when_dlq, dead_lettered_at, recovery_status, recovered_at, generation)
        VALUES (@id, 'wh_inbox', @src, 'T.A', '{}'::jsonb, 5, 3, NOW(), @st,
                CASE WHEN @rec THEN NOW() ELSE NULL END, 'seed/1')
        """, connection);
      cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
      cmd.Parameters.AddWithValue("src", Guid.CreateVersion7());
      cmd.Parameters.AddWithValue("st", status);
      cmd.Parameters.AddWithValue("rec", recovered);
      await cmd.ExecuteNonQueryAsync();
    }

    var depths = await _provider.GetQueueDepthsAsync();

    await Assert.That(depths["dead_letters_held"]).IsEqualTo(2)
      .Because("HoldForReview rows are the quarantine an operator must be able to see");
    await Assert.That(depths["dead_letters_pending"]).IsEqualTo(1)
      .Because("unrecovered Pending rows are the recovery backlog");
    await Assert.That(depths["dead_letters_failed"]).IsEqualTo(1)
      .Because("PermanentlyFailed rows are the operator-decision pile");
  }

  [Test]
  public async Task GetTableBloatRatiosAsync_SmallTables_AreNotReportedAsBloatedAsync() {
    // Heap bytes per live row on a nearly-empty table is dominated by fixed page overhead, so the
    // ratio it produces is enormous and says nothing about bloat. Reporting it would raise a bloat
    // alarm for a table measured in kilobytes on every fresh database -- and an alarm that always
    // fires is one operators stop reading, including on the day a real table is bloating.
    var ratios = await _provider.GetTableBloatRatiosAsync();

    await Assert.That(ratios.Count).IsEqualTo(0)
      .Because("no tracked table on a fresh database holds more than 1000 live rows, and below "
             + "that threshold the per-row average measures page overhead rather than bloat");
  }

  [Test]
  public async Task GetTableBloatRatiosAsync_TablePastTheThreshold_IsMeasuredAsync() {
    // The counterpart to the exclusion above: it must not be so broad that nothing is ever
    // measured. Rows written once and never updated carry no dead tuples, so a freshly filled
    // table should sit close to the width its rows need.
    await _fillOutboxAsync(1200);

    var ratios = await _provider.GetTableBloatRatiosAsync();

    await Assert.That(ratios.ContainsKey("wh_outbox")).IsTrue()
      .Because("a table past the live-row threshold is exactly what this metric exists to watch, "
             + "and an exclusion that also hid those would leave bloat undetectable");
    await Assert.That(ratios["wh_outbox"]).IsGreaterThan(0)
      .Because("the ratio of heap size to expected row width is positive for any table with rows");
    await Assert.That(ratios["wh_outbox"]).IsLessThan(100)
      .Because("freshly written rows carry no dead tuples, so the heap should be near the width "
             + "its rows need; a value this large would mean raw bytes leaked out as a ratio");
  }

  // ==== #1590: bytes per event type, sampled ====

  private const string ORDER_PLACED = "Shop.OrderPlaced, Shop";
  private const string EMBEDDING_CREATED = "Search.EmbeddingCreated, Search";

  [Test]
  public async Task SampleEventTypeBytesAsync_AtAFullSample_ReportsEachTypesBytesAsync() {
    await _seedEventAsync(ORDER_PLACED, """{"orderId":"o-1","total":12.5}""");
    await _seedEventAsync(ORDER_PLACED, """{"orderId":"o-2","total":7}""");
    // An older producer wrote the same type with its assembly version; it is the same type.
    await _seedEventAsync("Shop.OrderPlaced, Shop, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", """{"orderId":"o-3","total":1}""");
    await _seedEventAsync(EMBEDDING_CREATED, "{\"vector\":[" + string.Join(',', Enumerable.Range(0, 400)) + "]}");
    var stored = await _storedBytesAsync();

    var sample = await _provider.SampleEventTypeBytesAsync(new EventTypeSampleRequest("probe:full", 100, 50));

    await Assert.That(sample).IsNotNull();
    string[] reported = [EMBEDDING_CREATED, ORDER_PLACED];
    await Assert.That(sample!.Keys.Order(StringComparer.Ordinal).ToList()).IsEquivalentTo(reported)
      .Because("the version-decorated name is the same type, so it is reported with the others rather than as a fourth series");
    var orders = stored.Where(r => r.Type.StartsWith("Shop.", StringComparison.Ordinal)).ToList();
    await Assert.That(sample[ORDER_PLACED].SampledRows).IsEqualTo(3L);
    await Assert.That(sample[ORDER_PLACED].MeanDataBytes).IsEqualTo(orders.Average(r => (double)r.Data));
    await Assert.That(sample[ORDER_PLACED].MeanMetadataBytes).IsEqualTo(orders.Average(r => (double)r.Metadata));
    await Assert.That(sample[ORDER_PLACED].EstimatedTotalBytes).IsEqualTo(orders.Sum(r => r.Data + r.Metadata))
      .Because("a full sample reads every event, so the estimate is the stored total exactly");
    var embedding = stored.Single(r => r.Type == EMBEDDING_CREATED);
    await Assert.That(sample[EMBEDDING_CREATED].MeanDataBytes).IsEqualTo((double)embedding.Data)
      .Because("the size is what the row stores, after compression, which is what the disk pays for");
  }

  [Test]
  public async Task SampleEventTypeBytesAsync_PastTheCap_SumsTheSmallestUnderOtherAsync() {
    await _seedEventAsync(ORDER_PLACED, """{"orderId":"o-1"}""");
    await _seedEventAsync(ORDER_PLACED, """{"orderId":"o-2"}""");
    await _seedEventAsync(EMBEDDING_CREATED, "{\"vector\":[" + string.Join(',', Enumerable.Range(0, 400)) + "]}");
    var stored = await _storedBytesAsync();

    var sample = await _provider.SampleEventTypeBytesAsync(new EventTypeSampleRequest("probe:cap", 100, 1));

    string[] reported = [TableStatisticsOptions.OTHER_EVENT_TYPES, EMBEDDING_CREATED];
    await Assert.That(sample!.Keys.Order(StringComparer.Ordinal).ToList())
      .IsEquivalentTo(reported)
      .Because("the largest type by bytes keeps its name; one many-times-smaller type is what other is for");
    await Assert.That(sample[TableStatisticsOptions.OTHER_EVENT_TYPES].SampledRows).IsEqualTo(2L);
    await Assert.That(sample.Values.Sum(t => t.EstimatedTotalBytes)).IsEqualTo(stored.Sum(r => r.Data + r.Metadata))
      .Because("folding changes the names, never the total");
  }

  [Test]
  public async Task SampleEventTypeBytesAsync_ASecondClaimOfTheWindow_ReadsNothingAsync() {
    await _seedEventAsync(ORDER_PLACED, """{"orderId":"o-1"}""");
    var other = new PostgresTableStatisticsProvider(_dataSource);

    var first = await _provider.SampleEventTypeBytesAsync(new EventTypeSampleRequest("probe:window", 100, 50));
    var second = await other.SampleEventTypeBytesAsync(new EventTypeSampleRequest("probe:window", 100, 50));

    await Assert.That(first).IsNotNull();
    await Assert.That(second).IsNull()
      .Because("the window is sampled once for the fleet; the instance that lost the claim reads nothing");
  }

  [Test]
  [Arguments(0d, 50)]
  [Arguments(-1d, 50)]
  [Arguments(100.5d, 50)]
  [Arguments(1d, 0)]
  public async Task SampleEventTypeBytesAsync_RefusesAShareOrCapNoSampleCouldHonorAsync(double percent, int cap) {
    await Assert.That(async () => await _provider.SampleEventTypeBytesAsync(new EventTypeSampleRequest("probe:refused", percent, cap)))
      .Throws<ArgumentOutOfRangeException>();
  }

  private async Task _runAsync(params string[] statements) {
    await using var connection = await _dataSource.OpenConnectionAsync();
    foreach (var sql in statements) {
      await using var cmd = new NpgsqlCommand(sql, connection);
      await cmd.ExecuteNonQueryAsync();
    }
  }

  private async Task<long> _scalarAsync(string sql) {
    await using var connection = await _dataSource.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(sql, connection);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private async Task<List<string>> _scalarListAsync(string sql) {
    var values = new List<string>();
    await using var connection = await _dataSource.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(sql, connection);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      values.Add(reader.GetString(0));
    }
    return values;
  }

  /// <summary>
  /// Writes <paramref name="rows"/> outbox rows and refreshes the planner statistics the bloat
  /// query reads.
  /// </summary>
  private async Task _fillOutboxAsync(int rows) {
    await using var db = CreateDbContext();
    var payload = JsonDocument.Parse("{}").RootElement;
    for (var i = 0; i < rows; i++) {
      var id = (Guid)TrackedGuid.New();
      db.Outbox.Add(new OutboxRecord {
        MessageId = id,
        MessageType = "TestMessage, TestAssembly",
        MessageData = new OutboxMessageData {
          MessageId = MessageId.From(id),
          Payload = payload,
          Hops = []
        },
        Metadata = new EnvelopeMetadata { MessageId = MessageId.From(id), Hops = [] },
        CreatedAt = DateTimeOffset.UtcNow
      });
    }
    await db.SaveChangesAsync();

    // The query reads pg_stat_user_tables.n_live_tup and pg_stats.avg_width, and ANALYZE is what
    // populates both. Without it the rows are present but the catalog has not seen their shape,
    // so the table reads as below the threshold it just passed and the test would be asserting
    // against an empty result for the wrong reason.
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var analyze = new NpgsqlCommand("ANALYZE wh_outbox;", connection);
    await analyze.ExecuteNonQueryAsync();
  }

  private async Task _seedEventAsync(string eventType, string payload) {
    var eventId = (Guid)TrackedGuid.New();
    var streamId = (Guid)TrackedGuid.New();
    await using var connection = await _dataSource.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("""
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at)
      VALUES (@evt, @stream, @stream, 'agg', @type, '{}'::jsonb, 1, NOW());
      INSERT INTO wh_event_body (event_id, event_data, metadata)
      VALUES (@evt, @data::jsonb, '{"MessageId":"m","Hops":[]}'::jsonb)
      """, connection);
    cmd.Parameters.AddWithValue("evt", eventId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("type", eventType);
    cmd.Parameters.AddWithValue("data", payload);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Every stored event's type and the bytes its row stores, read without sampling.</summary>
  private async Task<List<(string Type, long Data, long Metadata)>> _storedBytesAsync() {
    var rows = new List<(string Type, long Data, long Metadata)>();
    await using var connection = await _dataSource.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("""
      SELECT es.event_type, pg_column_size(b.event_data)::bigint, pg_column_size(b.metadata)::bigint
      FROM wh_event_body b JOIN wh_event_store es ON es.event_id = b.event_id
      """, connection);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
    }
    return rows;
  }
}
