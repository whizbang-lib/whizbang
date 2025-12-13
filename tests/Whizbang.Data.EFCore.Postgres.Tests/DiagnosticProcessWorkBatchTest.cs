using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Diagnostic test to understand what's happening with message completion and reclaiming.
/// </summary>
public class DiagnosticProcessWorkBatchTest : EFCoreTestBase {
  [Test]
  public async Task Diagnostic_InvestigateCompletionReclaimingAsync() {
    // Subscribe to PostgreSQL NOTICE messages
    var noticeDbContext = CreateDbContext();
    var connection = (Npgsql.NpgsqlConnection)noticeDbContext.Database.GetDbConnection();
    connection.Notice += (sender, args) => {
      Console.WriteLine($"[POSTGRES NOTICE] {args.Notice.MessageText}");
    };

    var instanceId = Guid.CreateVersion7();
    var workCoordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(noticeDbContext, JsonContextRegistry.CreateCombinedOptions());

    // Store first message
    var messageId1 = MessageId.New();
    var streamId = Guid.CreateVersion7();

    Console.WriteLine($"=== STEP 1: Storing messageId1: {messageId1.Value} ===");

    var workBatch1 = await workCoordinator.ProcessWorkBatchAsync(
      instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [
        new OutboxMessage {
          MessageId = messageId1.Value,
          Destination = "test-topic",
          EventType = "TestEvent",
          EventData = "{\"data\":\"first\"}",
          Metadata = "{\"hops\":[]}",
          Scope = null,
          IsEvent = true,
          StreamId = streamId
        }
      ],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []
    );

    Console.WriteLine($"WorkBatch1 returned {workBatch1.OutboxWork.Count} messages");

    // Query database to see status of messageId1
    var dbContext = CreateDbContext();
    var msg1AfterStore = await dbContext.Database.SqlQueryRaw<DiagnosticOutboxRecord>(
      "SELECT message_id, status, instance_id, lease_expiry FROM wh_outbox WHERE message_id = {0}",
      messageId1.Value
    ).FirstOrDefaultAsync();

    Console.WriteLine($"After STEP 1:");
    Console.WriteLine($"  message_id: {msg1AfterStore?.MessageId}");
    Console.WriteLine($"  status: {msg1AfterStore?.Status}");
    Console.WriteLine($"  instance_id: {msg1AfterStore?.InstanceId}");
    Console.WriteLine($"  lease_expiry: {msg1AfterStore?.LeaseExpiry}");

    // Now report completion AND store a second message
    var messageId2 = MessageId.New();

    Console.WriteLine($"\n=== STEP 2: Reporting messageId1 complete, storing messageId2: {messageId2.Value} ===");
    Console.WriteLine($"p_outbox_completions: [{{\"messageId\":\"{messageId1.Value}\",\"status\":4}}]");

    var workBatch2 = await workCoordinator.ProcessWorkBatchAsync(
      instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [
        new MessageCompletion {
          MessageId = messageId1.Value,
          Status = MessageProcessingStatus.Published
        }
      ],
      outboxFailures: [],
      inboxCompletions: [],
      inboxFailures: [],
      receptorCompletions: [],
      receptorFailures: [],
      perspectiveCompletions: [],
      perspectiveFailures: [],
      newOutboxMessages: [
        new OutboxMessage {
          MessageId = messageId2.Value,
          Destination = "test-topic",
          EventType = "TestEvent",
          EventData = "{\"data\":\"second\"}",
          Metadata = "{\"hops\":[]}",
          Scope = null,
          IsEvent = true,
          StreamId = streamId
        }
      ],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []
    );

    Console.WriteLine($"WorkBatch2 returned {workBatch2.OutboxWork.Count} messages:");
    foreach (var work in workBatch2.OutboxWork) {
      Console.WriteLine($"  - {work.MessageId}");
    }

    // Query database to see final status of both messages
    var msg1AfterCompletion = await dbContext.Database.SqlQueryRaw<DiagnosticOutboxRecord>(
      "SELECT message_id, status, instance_id, lease_expiry FROM wh_outbox WHERE message_id = {0}",
      messageId1.Value
    ).FirstOrDefaultAsync();

    var msg2AfterStore = await dbContext.Database.SqlQueryRaw<DiagnosticOutboxRecord>(
      "SELECT message_id, status, instance_id, lease_expiry FROM wh_outbox WHERE message_id = {0}",
      messageId2.Value
    ).FirstOrDefaultAsync();

    Console.WriteLine($"\nAfter STEP 2:");
    Console.WriteLine($"messageId1 ({messageId1.Value}):");
    if (msg1AfterCompletion != null) {
      Console.WriteLine($"  status: {msg1AfterCompletion.Status}");
      Console.WriteLine($"  instance_id: {msg1AfterCompletion.InstanceId}");
      Console.WriteLine($"  lease_expiry: {msg1AfterCompletion.LeaseExpiry}");
    } else {
      Console.WriteLine($"  DELETED from database");
    }

    Console.WriteLine($"messageId2 ({messageId2.Value}):");
    Console.WriteLine($"  status: {msg2AfterStore?.Status}");
    Console.WriteLine($"  instance_id: {msg2AfterStore?.InstanceId}");
    Console.WriteLine($"  lease_expiry: {msg2AfterStore?.LeaseExpiry}");

    // Assertions to understand the behavior
    await Assert.That(workBatch2.OutboxWork.Count).IsEqualTo(1)
      .Because("Only messageId2 should be returned, not messageId1 which was just completed");

    if (workBatch2.OutboxWork.Count == 2) {
      Console.WriteLine("\n❌ BUG REPRODUCED: messageId1 was reclaimed even though it was in p_outbox_completions");
    }
  }
}

public class DiagnosticOutboxRecord {
  [Column("message_id")]
  public Guid MessageId { get; set; }

  [Column("status")]
  public int Status { get; set; }

  [Column("instance_id")]
  public Guid? InstanceId { get; set; }

  [Column("lease_expiry")]
  public DateTimeOffset? LeaseExpiry { get; set; }
}
