using Npgsql;
using TUnit.Assertions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for failure_reason column in outbox and inbox tables.
/// Verifies schema migration 009_AddFailureReasonColumn.sql.
/// </summary>
[Category("Shard1")]
public class FailureReasonSchemaTests : EFCoreTestBase {
  [Test]
  public async Task OutboxTable_ShouldHaveFailureReasonColumnAsync() {
    // Arrange
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    // Act - Query for failure_reason column in wh_outbox
    const string sql = @"
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

    // Act - failure_reason is work state, so migration 162 moved it to wh_inbox_state along with
    // the rest of what a claim rewrites. The property is unchanged -- the column exists, typed and
    // defaulted as before -- so the query follows it rather than the test being dropped.
    const string sql = @"
      SELECT column_name, data_type, column_default
      FROM information_schema.columns
      WHERE table_name = 'wh_inbox_state'
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

  /// <summary>
  /// No index is maintained on the outbox's failure reason either, for the same reasons and one more.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <c>idx_outbox_failure_reason</c> keyed failure_reason and predicated on the status bit
  /// <c>32768</c>. The outbox stopped discriminating on status bits and discriminates on
  /// <c>processed_at</c>, and a partial index is considered only when Postgres can prove the query's
  /// predicate implies the index's -- textually, not arithmetically. So this index was not merely
  /// unused on current data: no query the outbox issues could reach it, whatever the data looked
  /// like. A deployed fleet carried it at zero scans through an entire bulk import.
  /// </para>
  /// <para>
  /// Unreachable is not free. It was still maintained on every insert, update and delete the outbox
  /// took, and the producer side of a bulk load is the hottest write path in the system. Migration
  /// 164 drops it, and the descriptor that recreated it on every boot no longer declares it.
  /// </para>
  /// <para>
  /// Asserted as an absence for the same reason the inbox one below is: a test that simply
  /// disappears takes the decision with it. The rule that keeps the next one from being written
  /// against the retired shape lives in OutboxIndexesMatchLiveQueriesTests.
  /// </para>
  /// </remarks>
  [Test]
  public async Task OutboxTable_FailureReason_IsDeliberatelyNotIndexedAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    const string sql = @"
      SELECT indexname
      FROM pg_indexes
      WHERE tablename = 'wh_outbox'
        AND indexdef LIKE '%failure_reason%'";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    var found = new List<string>();
    while (await reader.ReadAsync()) {
      found.Add(reader.GetString(0));
    }

    await Assert.That(found).IsEmpty()
      .Because("the index was partial on a status bitmask no outbox query can reach, so it could "
        + "never be chosen while still costing every outbox write. Found: "
        + string.Join(", ", found));
  }

  /// <summary>
  /// No index is maintained on the inbox's failure reason, on either table, and that is a decision.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The old <c>idx_inbox_failure_reason</c> keyed failure_reason and predicated on status, and both
  /// columns moved to wh_inbox_state in migration 162. It was one of the indexes the cutover dropped
  /// WITHOUT recreating, because nothing queries the inbox by failure reason: the column is read on a
  /// row already found by message id, and the diagnostics that group by it are ad hoc rather than on
  /// a hot path. An index costs a write on every claim, lease renewal, retry and completion, which is
  /// the entire cost the split exists to remove, so one that serves no query is not free.
  /// </para>
  /// <para>
  /// Asserted as an absence rather than deleted, because a test that simply disappears takes the
  /// decision with it, and the next person to see failure_reason unindexed has no way to tell a
  /// deliberate removal from an oversight. If a query ever needs it, this test is the place that says
  /// what changed -- and the state table's index count is gated in the performance baseline, so
  /// adding one is a visible trade rather than a quiet one.
  /// </para>
  /// </remarks>
  [Test]
  public async Task InboxTable_FailureReason_IsDeliberatelyNotIndexedAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    const string sql = @"
      SELECT indexname
      FROM pg_indexes
      WHERE tablename IN ('wh_inbox', 'wh_inbox_state')
        AND indexdef LIKE '%failure_reason%'";

    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    var found = new List<string>();
    while (await reader.ReadAsync()) {
      found.Add(reader.GetString(0));
    }

    await Assert.That(found).IsEmpty()
      .Because("the cutover dropped idx_inbox_failure_reason and deliberately did not recreate it: "
        + "no query reaches the inbox by failure reason, and an index on the work-state table is paid "
        + "for on every claim, renewal, retry and completion. Found: " + string.Join(", ", found));
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
      const string insertSql = @"
        INSERT INTO wh_outbox (
          message_id, destination, message_type, event_data, metadata,
          status, created_at, failure_reason
        ) VALUES (
          @message_id, 'test-topic', 'TestEvent', '{}', '{}',
          1, NOW(), @failure_reason
        )
        ON CONFLICT (message_id) DO UPDATE SET failure_reason = @failure_reason";

      await using var insertCommand = new NpgsqlCommand(insertSql, connection);
      insertCommand.Parameters.AddWithValue("message_id", messageId);
      insertCommand.Parameters.AddWithValue("failure_reason", reasonValue);
      await insertCommand.ExecuteNonQueryAsync();

      // Verify value was stored correctly
      const string selectSql = "SELECT failure_reason FROM wh_outbox WHERE message_id = @message_id";
      await using var selectCommand = new NpgsqlCommand(selectSql, connection);
      selectCommand.Parameters.AddWithValue("message_id", messageId);
      var storedValue = (int)(await selectCommand.ExecuteScalarAsync() ?? -1);

      await Assert.That(storedValue).IsEqualTo(reasonValue);
    }

    // Cleanup
    const string deleteSql = "DELETE FROM wh_outbox WHERE message_id = @message_id";
    await using var deleteCommand = new NpgsqlCommand(deleteSql, connection);
    deleteCommand.Parameters.AddWithValue("message_id", messageId);
    await deleteCommand.ExecuteNonQueryAsync();
  }
}
