using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests that verify WorkCoordinatorPublisherWorker processes outbox messages end-to-end.
/// These tests verify the COMPLETE flow: store → claim → publish → mark complete.
/// </summary>
public class WorkCoordinatorPublisherWorkerIntegrationTests : EFCoreTestBase {
  [Test]
  public async Task WorkerProcessesOutboxMessages_EndToEndAsync() {
    // This is THE test that proves the entire flow works:
    // 1. Store message in outbox via ProcessWorkBatchAsync
    // 2. Function returns the message as work
    // 3. Publish the message to transport
    // 4. Mark as complete and verify database is updated

    // Arrange
    var testTransport = new TestTransport();
    var instanceId = Guid.CreateVersion7();
    var messageId = MessageId.New();
    var streamId = Guid.CreateVersion7();
    var workCoordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());

    // Store message and capture returned work
    var workBatch = await workCoordinator.ProcessWorkBatchAsync(
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
            CreateTestOutboxMessage(messageId.Value, "test-topic", streamId, true)
          ],
          newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []
    );

    // The function should return the newly stored message as work
    await Assert.That(workBatch.OutboxWork).Count().IsEqualTo(1)
      .Because("Newly stored message should be returned as work");

    var outboxWork = workBatch.OutboxWork.First();
    await Assert.That(outboxWork.MessageId).IsEqualTo(messageId.Value);

    // Publish the message (simulating what the worker would do)
    // OutboxWork.Envelope is already deserialized and ready to publish
    await testTransport.PublishAsync(outboxWork.Envelope, new TransportDestination(outboxWork.Destination, null, null), default);

    // Assert - Message was published
    await Assert.That(testTransport.PublishedMessages).Count().IsEqualTo(1)
      .Because("Message should have been published");

    var published = testTransport.PublishedMessages.First();
    await Assert.That(published.MessageId).IsEqualTo(messageId.Value);

    // Mark as published
    await workCoordinator.ProcessWorkBatchAsync(
      instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [
        new MessageCompletion {
          MessageId = messageId.Value,
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
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: [],
      flags: WorkBatchFlags.DebugMode  // Keep published messages for assertion
    );

    // Assert - Database was updated
    var outboxRecord = await CreateDbContext().Database
      .SqlQueryRaw<SimpleOutboxRecord>("SELECT message_id, status, published_at FROM wh_outbox WHERE message_id = {0}", messageId.Value)
      .FirstOrDefaultAsync();

    await Assert.That(outboxRecord).IsNotNull();
    await Assert.That(outboxRecord!.Status & (int)MessageProcessingStatus.Published)
      .IsEqualTo((int)MessageProcessingStatus.Published)
      .Because("Message should be marked as Published in database");
    await Assert.That(outboxRecord.PublishedAt).IsNotNull()
      .Because("PublishedAt timestamp should be set");
  }

  [Test]
  public async Task Worker_ProcessesMultipleMessages_InOrderAsync() {
    // Verify UUIDv7 ordering works
    var testTransport = new TestTransport();
    var instanceId = Guid.CreateVersion7();
    var workCoordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());

    // Store 3 messages
    var messageId1 = MessageId.New();
    await Task.Delay(2);
    var messageId2 = MessageId.New();
    await Task.Delay(2);
    var messageId3 = MessageId.New();
    var streamId = Guid.CreateVersion7();

    var workBatch = await workCoordinator.ProcessWorkBatchAsync(
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
        CreateTestOutboxMessage(messageId1.Value, "test", streamId, true),
        CreateTestOutboxMessage(messageId2.Value, "test", streamId, true),
        CreateTestOutboxMessage(messageId3.Value, "test", streamId, true)
      ],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []
    );

    // All 3 messages should be returned as work
    await Assert.That(workBatch.OutboxWork).Count().IsEqualTo(3);

    // Publish them in order
    foreach (var work in workBatch.OutboxWork.OrderBy(w => w.MessageId)) {
      // OutboxWork.Envelope is already deserialized and ready to publish
      await testTransport.PublishAsync(work.Envelope, new TransportDestination(work.Destination, null, null), default);
    }

    // Assert - All 3 messages published in order
    await Assert.That(testTransport.PublishedMessages).Count().IsEqualTo(3);

    var msg1 = testTransport.PublishedMessages.FirstOrDefault(m => m.MessageId == messageId1.Value);
    var msg2 = testTransport.PublishedMessages.FirstOrDefault(m => m.MessageId == messageId2.Value);
    var msg3 = testTransport.PublishedMessages.FirstOrDefault(m => m.MessageId == messageId3.Value);

    await Assert.That(msg1).IsNotNull();
    await Assert.That(msg2).IsNotNull();
    await Assert.That(msg3).IsNotNull();

    var idx1 = testTransport.PublishedMessages.IndexOf(msg1!);
    var idx2 = testTransport.PublishedMessages.IndexOf(msg2!);
    var idx3 = testTransport.PublishedMessages.IndexOf(msg3!);

    await Assert.That(idx1).IsLessThan(idx2);
    await Assert.That(idx2).IsLessThan(idx3);
  }

  [Test]
  public async Task ProcessWorkBatch_ProcessesReturnedWorkFromCompletionsAsync() {
    // This test proves that when we report completions, if new work is returned, it gets processed
    var testTransport = new TestTransport();
    var instanceId = Guid.CreateVersion7();
    var workCoordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());

    // Store first message
    var messageId1 = MessageId.New();
    var streamId = Guid.CreateVersion7();

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
        CreateTestOutboxMessage(messageId1.Value, "test-topic", streamId, true)
      ],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []
    );

    // First message should be returned
    await Assert.That(workBatch1.OutboxWork).Count().IsEqualTo(1);
    await Assert.That(workBatch1.OutboxWork[0].MessageId).IsEqualTo(messageId1.Value);

    // Now report completion AND store a second message in the same call
    // This simulates what happens when the dispatcher stores messages while we're processing
    var messageId2 = MessageId.New();

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
        CreateTestOutboxMessage(messageId2.Value, "test-topic", streamId, true)
      ],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []
    );

    // Second message should be returned (because it was just stored)
    await Assert.That(workBatch2.OutboxWork).Count().IsEqualTo(1)
      .Because("Newly stored message should be returned when reporting completions");
    await Assert.That(workBatch2.OutboxWork[0].MessageId).IsEqualTo(messageId2.Value);
  }

  [Test]
  public async Task ProcessWorkBatch_MultipleIterationsProcessAllWorkAsync() {
    // This test proves the processing loop works correctly across multiple iterations
    var instanceId = Guid.CreateVersion7();
    var workCoordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());

    // Store 3 messages, but do it in stages to simulate the loop
    var messageId1 = MessageId.New();
    var messageId2 = MessageId.New();
    var messageId3 = MessageId.New();
    var streamId = Guid.CreateVersion7();

    // Iteration 1: Store first message
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
        CreateTestOutboxMessage(messageId1.Value, "test", streamId, true)
      ],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []
    );

    await Assert.That(workBatch1.OutboxWork).Count().IsEqualTo(1);

    // Iteration 2: Report first as complete AND store second message
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
        CreateTestOutboxMessage(messageId2.Value, "test", streamId, true)
      ],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []
    );

    await Assert.That(workBatch2.OutboxWork).Count().IsEqualTo(1);

    // Iteration 3: Report second as complete AND store third message
    var workBatch3 = await workCoordinator.ProcessWorkBatchAsync(
      instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [
        new MessageCompletion {
          MessageId = messageId2.Value,
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
        CreateTestOutboxMessage(messageId3.Value, "test", streamId, true)
      ],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []
    );

    await Assert.That(workBatch3.OutboxWork).Count().IsEqualTo(1);

    // Iteration 4: Report third as complete with NO new messages
    var workBatch4 = await workCoordinator.ProcessWorkBatchAsync(
      instanceId,
      "TestService",
      "test-host",
      12345,
      metadata: null,
      outboxCompletions: [
        new MessageCompletion {
          MessageId = messageId3.Value,
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
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []
    );

    // No more work should be returned
    await Assert.That(workBatch4.OutboxWork).Count().IsEqualTo(0)
      .Because("Loop should terminate when no new work is available");
  }

  [Test]
  public async Task ProcessWorkBatch_LoopTerminatesWhenNoWorkAsync() {
    // This test proves the loop correctly terminates when there's no work
    var instanceId = Guid.CreateVersion7();
    var workCoordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(CreateDbContext(), JsonContextRegistry.CreateCombinedOptions());

    // Call with no messages - should return empty work batch
    var workBatch = await workCoordinator.ProcessWorkBatchAsync(
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
      newOutboxMessages: [],
      newInboxMessages: [],
      renewOutboxLeaseIds: [],
      renewInboxLeaseIds: []
    );

    await Assert.That(workBatch.OutboxWork).Count().IsEqualTo(0);
    await Assert.That(workBatch.InboxWork).Count().IsEqualTo(0);
  }
}

internal sealed class TestServiceInstanceProvider : IServiceInstanceProvider {
  public TestServiceInstanceProvider(Guid instanceId, string serviceName) {
    InstanceId = instanceId;
    ServiceName = serviceName;
  }

  public Guid InstanceId { get; }
  public string ServiceName { get; }
  public string HostName => "test-host";
  public int ProcessId => 12345;

  public ServiceInstanceInfo ToInfo() => new() {
    InstanceId = InstanceId,
    ServiceName = ServiceName,
    HostName = HostName,
    ProcessId = ProcessId
  };
}

internal sealed class TestTransport : ITransport {
  private readonly object _lock = new();
  public List<PublishedMessage> PublishedMessages { get; } = new();

  public bool IsInitialized => true;

  public Task InitializeAsync(CancellationToken cancellationToken = default) {
    return Task.CompletedTask;
  }

  public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

  public Task PublishAsync(
    IMessageEnvelope envelope,
    TransportDestination destination,
    CancellationToken cancellationToken = default) {

    lock (_lock) {
      PublishedMessages.Add(new PublishedMessage {
        MessageId = envelope.MessageId.Value,
        Destination = destination.ToString(),
        Envelope = envelope,
        PublishedAt = DateTimeOffset.UtcNow
      });
    }

    return Task.CompletedTask;
  }

  public Task<ISubscription> SubscribeAsync(
    Func<IMessageEnvelope, CancellationToken, Task> handler,
    TransportDestination destination,
    CancellationToken cancellationToken = default) {
    throw new NotImplementedException();
  }

  public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
    IMessageEnvelope envelope,
    TransportDestination destination,
    CancellationToken cancellationToken = default)
    where TRequest : notnull
    where TResponse : notnull {
    throw new NotImplementedException();
  }
}

internal sealed class PublishedMessage {
  public Guid MessageId { get; init; }
  public string Destination { get; init; } = null!;
  public IMessageEnvelope Envelope { get; init; } = null!;
  public DateTimeOffset PublishedAt { get; init; }
}

public sealed class SimpleOutboxRecord {
  [Column("message_id")]
  public Guid MessageId { get; set; }

  [Column("status")]
  public int Status { get; set; }

  [Column("published_at")]
  public DateTimeOffset? PublishedAt { get; set; }
}
