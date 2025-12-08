using Npgsql;
using TUnit.Assertions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for failure_reason column in outbox and inbox tables.
/// Verifies schema migration 009_AddFailureReasonColumn.sql.
/// </summary>
public class FailureReasonSchemaTests : EFCoreTestBase {
  [Test]
  public async Task OutboxTable_ShouldHaveFailureReasonColumnAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Act - Query for failure_reason column in wh_outbox
    var sql = @"
      SELECT column_name, data_type, column_default
      FROM information_schema.columns
      WHERE table_name = 'wh_outbox'
        AND column_name = 'failure_reason'";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    // Assert - Column should exist
    var columnExists = await reader.ReadAsync();
    await Assert.That(columnExists).IsTrue();

    if (columnExists) {
      var columnName = reader.GetString(0);
      var dataType = reader.GetString(1);
      var columnDefault = reader.IsDBNull(2) ? null : reader.GetString(2);

      await Assert.That(columnName).IsEqualTo("failure_reason");
      await Assert.That(dataType).IsEqualTo("integer");
      await Assert.That(columnDefault).IsNotNull();
      await Assert.That(columnDefault).Contains("99");  // Default value Unknown
    }
  }

  [Test]
  public async Task InboxTable_ShouldHaveFailureReasonColumnAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Act - Query for failure_reason column in wh_inbox
    var sql = @"
      SELECT column_name, data_type, column_default
      FROM information_schema.columns
      WHERE table_name = 'wh_inbox'
        AND column_name = 'failure_reason'";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    // Assert - Column should exist
    var columnExists = await reader.ReadAsync();
    await Assert.That(columnExists).IsTrue();

    if (columnExists) {
      var columnName = reader.GetString(0);
      var dataType = reader.GetString(1);
      var columnDefault = reader.IsDBNull(2) ? null : reader.GetString(2);

      await Assert.That(columnName).IsEqualTo("failure_reason");
      await Assert.That(dataType).IsEqualTo("integer");
      await Assert.That(columnDefault).IsNotNull();
      await Assert.That(columnDefault).Contains("99");  // Default value Unknown
    }
  }

  [Test]
  public async Task OutboxTable_FailureReasonIndex_ShouldExistAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Act - Query for index on failure_reason
    var sql = @"
      SELECT indexname
      FROM pg_indexes
      WHERE tablename = 'wh_outbox'
        AND indexname = 'idx_outbox_failure_reason'";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    // Assert - Index should exist
    var indexExists = await reader.ReadAsync();
    await Assert.That(indexExists).IsTrue();
  }

  [Test]
  public async Task InboxTable_FailureReasonIndex_ShouldExistAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Act - Query for index on failure_reason
    var sql = @"
      SELECT indexname
      FROM pg_indexes
      WHERE tablename = 'wh_inbox'
        AND indexname = 'idx_inbox_failure_reason'";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    // Assert - Index should exist
    var indexExists = await reader.ReadAsync();
    await Assert.That(indexExists).IsTrue();
  }

  [Test]
  public async Task FailureReasonColumn_CanStoreAllEnumValuesAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Act - Insert test message with each failure reason value
    var messageId = Guid.NewGuid();
    var testValues = new[] { 0, 1, 2, 3, 4, 5, 6, 99 };

    foreach (var reasonValue in testValues) {
      var insertSql = @"
        INSERT INTO wh_outbox (
          id, destination, event_type, event_data, metadata,
          status, created_at, failure_reason
        ) VALUES (
          @id, 'test-topic', 'TestEvent', '{}', '{}',
          1, NOW(), @failure_reason
        )
        ON CONFLICT (id) DO UPDATE SET failure_reason = @failure_reason";

      await using var insertCommand = new NpgsqlCommand(insertSql, connection);
      insertCommand.Parameters.AddWithValue("id", messageId);
      insertCommand.Parameters.AddWithValue("failure_reason", reasonValue);
      await insertCommand.ExecuteNonQueryAsync();

      // Verify value was stored correctly
      var selectSql = "SELECT failure_reason FROM wh_outbox WHERE id = @id";
      await using var selectCommand = new NpgsqlCommand(selectSql, connection);
      selectCommand.Parameters.AddWithValue("id", messageId);
      var storedValue = (int)(await selectCommand.ExecuteScalarAsync() ?? -1);

      await Assert.That(storedValue).IsEqualTo(reasonValue);
    }

    // Cleanup
    var deleteSql = "DELETE FROM wh_outbox WHERE id = @id";
    await using var deleteCommand = new NpgsqlCommand(deleteSql, connection);
    deleteCommand.Parameters.AddWithValue("id", messageId);
    await deleteCommand.ExecuteNonQueryAsync();
  }
}
