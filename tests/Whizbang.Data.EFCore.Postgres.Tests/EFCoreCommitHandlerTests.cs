using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Phase B integration tests for handler-commit methods on <see cref="IWorkCoordinator"/>:
/// <see cref="IWorkCoordinator.CommitHandlerResultAsync"/> and
/// <see cref="IWorkCoordinator.CommitHandlerBatchAsync"/>.
/// The latter is the throughput multiplier (SAVEPOINT-per-handler isolation).
/// </summary>
/// <docs>fundamentals/work-coordinator/handler-commit</docs>
[Category("Shard4")]
public class EFCoreCommitHandlerTests : EFCoreTestBase {

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> Coord(WorkCoordinationDbContext ctx) =>
    new(ctx, JsonContextRegistry.CreateCombinedOptions());

  private static OutboxMessage MakeOutbox(Guid messageId, Guid streamId) =>
    CreateTestOutboxMessage(messageId, "out-topic", streamId);

  [Test]
  public async Task CommitHandlerResultAsync_HappyPath_MarksInboxAndStoresOutboxAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var inboxId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var emittedId = Guid.CreateVersion7();

    await using (var ins = conn.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
           instance_id, lease_expiry, stream_id, partition_number)
        VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', 1, 0, NOW(),
                @inst, NOW() + INTERVAL '60 seconds', @stream, 0)";
      ins.Parameters.AddWithValue("msg", inboxId);
      ins.Parameters.AddWithValue("inst", instanceId);
      ins.Parameters.AddWithValue("stream", streamId);
      await ins.ExecuteNonQueryAsync();
    }

    await coordinator.CommitHandlerResultAsync(new HandlerCommitRequest(
      HandlerId: Guid.NewGuid(),
      InstanceId: instanceId,
      ServiceName: "test",
      HostName: "test-host",
      ProcessId: 1,
      PartitionCount: 10000,
      InboxCompletion: new HandlerInboxCompletion(inboxId, Status: 4),
      NewOutboxMessages: [MakeOutbox(emittedId, streamId)]));

    await using (var verify = conn.CreateCommand()) {
      verify.CommandText = "SELECT processed_at IS NOT NULL FROM wh_inbox WHERE message_id = @msg";
      verify.Parameters.AddWithValue("msg", inboxId);
      await Assert.That((bool)(await verify.ExecuteScalarAsync())!).IsTrue();
    }
    await using (var verify = conn.CreateCommand()) {
      verify.CommandText = "SELECT count(*) FROM wh_outbox WHERE message_id = @msg";
      verify.Parameters.AddWithValue("msg", emittedId);
      await Assert.That((long)(await verify.ExecuteScalarAsync())!).IsEqualTo(1L);
    }
  }

  [Test]
  public async Task CommitHandlerBatchAsync_AllSucceed_ReturnsAllSuccessAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var inboxIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
    foreach (var id in inboxIds) {
      await using var ins = conn.CreateCommand();
      ins.CommandText = @"
        INSERT INTO wh_inbox
          (message_id, handler_name, message_type, event_data, metadata, status, attempts, received_at,
           instance_id, lease_expiry, stream_id, partition_number)
        VALUES (@msg, 'TestHandler', 'TestEvent', '{}', '{}', 1, 0, NOW(),
                @inst, NOW() + INTERVAL '60 seconds', @stream, 0)";
      ins.Parameters.AddWithValue("msg", id);
      ins.Parameters.AddWithValue("inst", instanceId);
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }

    var requests = inboxIds.Select(id => new HandlerCommitRequest(
      HandlerId: Guid.NewGuid(),
      InstanceId: instanceId,
      ServiceName: "test",
      HostName: "test-host",
      ProcessId: 1,
      PartitionCount: 10000,
      InboxCompletion: new HandlerInboxCompletion(id, Status: 4))).ToList();

    var results = await coordinator.CommitHandlerBatchAsync(requests);

    await Assert.That(results.Count).IsEqualTo(3);
    await Assert.That(results.All(r => r.Success)).IsTrue();
    await Assert.That(results.All(r => r.ErrorMessage == null)).IsTrue();
  }

  [Test]
  public async Task CommitHandlerBatchAsync_EmptyList_ReturnsEmptyResultAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = Coord(dbContext);
    var results = await coordinator.CommitHandlerBatchAsync([]);
    await Assert.That(results.Count).IsEqualTo(0);
  }
}
