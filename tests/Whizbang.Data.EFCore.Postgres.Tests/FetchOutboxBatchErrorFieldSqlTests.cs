using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Slice 1 of release/v0.648.0-alpha.1 — integration-locks the
/// <c>OutboxBatchRow.Error</c> plumbing across the
/// <c>fetch_outbox_batch</c> SQL function + <c>EFCoreWorkCoordinator</c>
/// mapping. Without this, the SQL function would return the column but the
/// C# mapper wouldn't read it (or vice versa) and the pre-publish DLQ gate
/// would silently fall back to the meta-message even when
/// <c>wh_outbox.error</c> is populated.
/// </summary>
/// <docs>operations/dead-letter-queue/internal-dlq</docs>
public class FetchOutboxBatchErrorFieldSqlTests : EFCoreTestBase {

  [Test]
  public async Task FetchOutboxBatch_RowWithErrorColumn_PopulatesErrorFieldAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();
    var leaseExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
    const string realError = """
      System.InvalidOperationException: Could not open connection to 'jdx_bff'
         at Whizbang.Data.EFCore.Postgres.Functions.OutboxClaim.LeaseAsync(Guid instanceId)
      """;

    await using (var insertCmd = conn.CreateCommand()) {
      insertCmd.CommandText = """
        INSERT INTO wh_outbox (
          message_id, stream_id, destination, message_type, envelope_type,
          event_data, metadata, status, attempts, partition_number, is_event,
          instance_id, lease_expiry, error
        ) VALUES (
          @id, @stream, 'test-topic', 'TestMessage', 'MessageEnvelope',
          '{}'::jsonb, '{}'::jsonb, 1, 11, 0, false,
          @inst, @lease, @err
        )
        """;
      insertCmd.Parameters.AddWithValue("id", messageId);
      insertCmd.Parameters.AddWithValue("stream", streamId);
      insertCmd.Parameters.AddWithValue("inst", instanceId);
      insertCmd.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.TimestampTz) { Value = leaseExpiry });
      insertCmd.Parameters.AddWithValue("err", realError);
      await insertCmd.ExecuteNonQueryAsync();
    }

    var coord = new Whizbang.Data.EFCore.Postgres.EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext,
      jsonOptions: JsonContextRegistry.CreateCombinedOptions(),
      logger: NullLogger<Whizbang.Data.EFCore.Postgres.EFCoreWorkCoordinator<WorkCoordinationDbContext>>.Instance);

    var rows = await coord.FetchOutboxBatchAsync([streamId], instanceId);

    await Assert.That(rows.Count).IsEqualTo(1)
      .Because("Setup precondition: exactly one row should be visible to FetchOutboxBatchAsync for the test stream.");
    await Assert.That(rows[0].Error).IsEqualTo(realError)
      .Because("Slice 1 invariant: fetch_outbox_batch SQL function returns the error column AND EFCoreWorkCoordinator maps it onto OutboxBatchRow.Error verbatim. End-to-end, the row carries its existing wh_outbox.error value into the worker so the pre-publish DLQ gate can use it as errorText.");
  }

  [Test]
  public async Task FetchOutboxBatch_RowWithNullError_LeavesErrorFieldNullAsync() {
    await using var dbContext = CreateDbContext();
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var messageId = (Guid)TrackedGuid.NewMedo();
    var leaseExpiry = DateTimeOffset.UtcNow.AddMinutes(5);

    await using (var insertCmd = conn.CreateCommand()) {
      insertCmd.CommandText = """
        INSERT INTO wh_outbox (
          message_id, stream_id, destination, message_type, envelope_type,
          event_data, metadata, status, attempts, partition_number, is_event,
          instance_id, lease_expiry
        ) VALUES (
          @id, @stream, 'test-topic', 'TestMessage', 'MessageEnvelope',
          '{}'::jsonb, '{}'::jsonb, 1, 0, 0, false,
          @inst, @lease
        )
        """;
      insertCmd.Parameters.AddWithValue("id", messageId);
      insertCmd.Parameters.AddWithValue("stream", streamId);
      insertCmd.Parameters.AddWithValue("inst", instanceId);
      insertCmd.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.TimestampTz) { Value = leaseExpiry });
      await insertCmd.ExecuteNonQueryAsync();
    }

    var coord = new Whizbang.Data.EFCore.Postgres.EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext,
      jsonOptions: JsonContextRegistry.CreateCombinedOptions(),
      logger: NullLogger<Whizbang.Data.EFCore.Postgres.EFCoreWorkCoordinator<WorkCoordinationDbContext>>.Instance);

    var rows = await coord.FetchOutboxBatchAsync([streamId], instanceId);

    await Assert.That(rows.Count).IsEqualTo(1);
    await Assert.That(rows[0].Error).IsNull()
      .Because("Fresh rows have no error captured; Error must be null in that case so the pre-publish gate falls back to the meta-message (preserving the legacy default behavior).");
  }
}
