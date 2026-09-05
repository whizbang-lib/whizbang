using System.Text.Json;
using Npgsql;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresTableStatisticsProvider"/> against real PostgreSQL.
/// Verifies pg_stat_user_tables and queue depth queries return correct data.
/// </summary>
/// <tests>src/Whizbang.Data.EFCore.Postgres/PostgresTableStatisticsProvider.cs</tests>
[Category("Integration")]
[Category("Shard2")]
public class PostgresTableStatisticsProviderTests : EFCoreTestBase {
  private PostgresTableStatisticsProvider _provider = null!;

  [Before(Test)]
  public async Task TestSetupAsync() {
    var dataSource = NpgsqlDataSource.Create(ConnectionString);
    _provider = new PostgresTableStatisticsProvider(dataSource);
    await Task.CompletedTask;
  }

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

  [Test]
  public async Task GetEstimatedTableSizesAsync_FreshDatabase_HasSevenTablesAsync() {
    var sizes = await _provider.GetEstimatedTableSizesAsync();
    var count = sizes.Count;
    await Assert.That(count).IsEqualTo(7)
      .Because("Should return sizes for all 7 tracked infrastructure tables");
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
      INSERT INTO wh_inbox (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at)
      VALUES (@messageId, 'TestHandler', 'TestType', '{}'::jsonb, '{}'::jsonb, 1, 0, NOW())
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

  /// <summary>
  /// Writes <paramref name="rows"/> outbox rows and refreshes the planner statistics the bloat
  /// query reads.
  /// </summary>
  private async Task _fillOutboxAsync(int rows) {
    await using var db = CreateDbContext();
    var payload = JsonDocument.Parse("{}").RootElement;
    for (var i = 0; i < rows; i++) {
      var id = (Guid)TrackedGuid.NewMedo();
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
}
