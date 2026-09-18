using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// That an outbox or inbox failure recorded in the shape the runtime writes keeps its reason.
/// </summary>
/// <remarks>
/// The runtime serializes a failure with <c>MessageId</c> and <c>Reason</c>. The outbox and inbox
/// failure functions read <c>FailureReason</c>, a name nothing writes, so every recorded reason
/// was Unknown and the dead-letter decision, which keys on the reason, could not tell a lease that
/// lapsed from a handler that threw. The perspective function was corrected first; these two
/// read both names the same way.
/// </remarks>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
[Category("Shard3")]
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class OutboxInboxFailureReasonSqlTests : EFCoreTestBase {
  private const int LEASE_EXPIRED = 6;

  private async Task<NpgsqlConnection> _openAsync(DbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _recordAsync(NpgsqlConnection connection, string function, Guid messageId) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = $"SELECT {function}(@failures::jsonb, NOW())";
    cmd.Parameters.AddWithValue("failures",
      $$"""[{"MessageId":"{{messageId}}","CompletedStatus":0,"Error":"lease lapsed","Reason":{{LEASE_EXPIRED}}}]""");
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<int> _reasonAsync(NpgsqlConnection connection, string table, Guid messageId) {
    await using var read = connection.CreateCommand();
    read.CommandText = $"SELECT failure_reason::int FROM {table} WHERE message_id = @id";
    read.Parameters.AddWithValue("id", messageId);
    return Convert.ToInt32(await read.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  [Test]
  public async Task OutboxFailure_InTheShapeTheRuntimeWrites_KeepsItsReasonAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var messageId = Guid.NewGuid();
    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at, stream_id, partition_number)
        VALUES (@msg, 'test-topic', 'TestEvent', '{}', '{}', 0, 1, NOW(), gen_random_uuid(), 0)";
      ins.Parameters.AddWithValue("msg", messageId);
      await ins.ExecuteNonQueryAsync();
    }

    await _recordAsync(connection, "process_outbox_failures", messageId);

    await Assert.That(await _reasonAsync(connection, "wh_outbox", messageId)).IsEqualTo(LEASE_EXPIRED)
      .Because("the reason is read from Reason, the name the runtime writes, not only from FailureReason");
  }

  [Test]
  public async Task InboxFailure_InTheShapeTheRuntimeWrites_KeepsItsReasonAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var messageId = Guid.NewGuid();
    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        WITH m AS (
          INSERT INTO wh_inbox
            (message_id, handler_name, message_type, event_data, metadata, received_at, stream_id)
          VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', NOW(), gen_random_uuid())
          RETURNING message_id, stream_id, received_at, priority, is_event
        )
        INSERT INTO wh_inbox_state
          (message_id, stream_id, received_at, priority, is_event, status, attempts,
           partition_number, failure_reason)
        SELECT message_id, stream_id, received_at, priority, is_event, 0, 1, 0, 99
        FROM m";
      ins.Parameters.AddWithValue("msg", messageId);
      await ins.ExecuteNonQueryAsync();
    }

    await _recordAsync(connection, "process_inbox_failures", messageId);

    await Assert.That(await _reasonAsync(connection, "wh_inbox_state", messageId)).IsEqualTo(LEASE_EXPIRED)
      .Because("the reason is read from Reason, the name the runtime writes, not only from FailureReason");
  }
}
