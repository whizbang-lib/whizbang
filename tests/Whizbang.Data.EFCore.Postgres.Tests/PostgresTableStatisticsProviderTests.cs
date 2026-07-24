using Npgsql;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresTableStatisticsProvider"/> against real PostgreSQL.
/// Verifies pg_stat_user_tables and queue depth queries return correct data.
/// </summary>
/// <tests>src/Whizbang.Data.EFCore.Postgres/PostgresTableStatisticsProvider.cs</tests>
[Category("Integration")]
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
}
