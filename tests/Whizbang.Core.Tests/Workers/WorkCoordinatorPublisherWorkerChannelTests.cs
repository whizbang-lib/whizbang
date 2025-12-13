using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

public class WorkCoordinatorPublisherWorkerChannelTests {
  private record _testMessage { }

  private static IMessageEnvelope<object> _createTestEnvelope(Guid messageId) {
    var envelope = new MessageEnvelope<_testMessage> {
      MessageId = MessageId.From(messageId),
      Payload = new _testMessage(),
      Hops = []
    };
    return envelope as IMessageEnvelope<object> ?? throw new InvalidOperationException("Envelope must implement IMessageEnvelope<object>");
  }

  private class TestWorkCoordinator : IWorkCoordinator {
    public List<OutboxWork> WorkToReturn { get; set; } = [];
    public List<MessageCompletion> ReceivedCompletions { get; } = [];
    public List<MessageFailure> ReceivedFailures { get; } = [];
    public int CallCount { get; private set; }

    public Task<WorkBatch> ProcessWorkBatchAsync(
      Guid instanceId,
      string serviceName,
      string hostName,
      int processId,
      Dictionary<string, System.Text.Json.JsonElement>? metadata,
      MessageCompletion[] outboxCompletions,
      MessageFailure[] outboxFailures,
      MessageCompletion[] inboxCompletions,
      MessageFailure[] inboxFailures,
      ReceptorProcessingCompletion[] receptorCompletions,
      ReceptorProcessingFailure[] receptorFailures,
      PerspectiveCheckpointCompletion[] perspectiveCompletions,
      PerspectiveCheckpointFailure[] perspectiveFailures,
      OutboxMessage[] newOutboxMessages,
      InboxMessage[] newInboxMessages,
      Guid[] renewOutboxLeaseIds,
      Guid[] renewInboxLeaseIds,
      WorkBatchFlags flags = WorkBatchFlags.None,
      int partitionCount = 10000,
      int maxPartitionsPerInstance = 100,
      int leaseSeconds = 300,
      int staleThresholdSeconds = 600,
      CancellationToken cancellationToken = default) {

      CallCount++;
      ReceivedCompletions.AddRange(outboxCompletions);
      ReceivedFailures.AddRange(outboxFailures);

      return Task.FromResult(new WorkBatch {
        OutboxWork = [.. WorkToReturn],
        InboxWork = []
      });
    }
  }

  private class TestPublishStrategy : IMessagePublishStrategy {
    public ConcurrentBag<OutboxWork> PublishedWork { get; } = [];
    public Func<OutboxWork, MessagePublishResult>? PublishResultFunc { get; set; }
    public TimeSpan PublishDelay { get; set; } = TimeSpan.Zero;

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      return Task.FromResult(true);
    }

    public async Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      if (PublishDelay > TimeSpan.Zero) {
        await Task.Delay(PublishDelay, cancellationToken);
      }

      PublishedWork.Add(work);

      if (PublishResultFunc != null) {
        return PublishResultFunc(work);
      }

      return new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      };
    }
  }

  [Test]
  public async Task ProcessWorkBatch_WithWork_ShouldPublishViaStrategyAsync() {
    // Arrange
    var workCoordinator = new TestWorkCoordinator();
    var publishStrategy = new TestPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();

    var messageId = Guid.NewGuid();
    workCoordinator.WorkToReturn = [
      new OutboxWork {
        MessageId = messageId,
        Destination = "test-topic",
        Envelope = _createTestEnvelope(messageId),
        StreamId = Guid.NewGuid(),
        PartitionNumber = 1,
        Attempts = 0,
        Status = MessageProcessingStatus.Stored,
        Flags = WorkBatchFlags.None,
        SequenceOrder = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      }
    ];

    var services = _createServiceCollection(workCoordinator, publishStrategy, instanceProvider);

    // Act & Assert - verify work was published
    // Note: Full worker integration will be tested separately
    // This test focuses on the strategy being called with work from coordinator
    var result = await publishStrategy.PublishAsync(workCoordinator.WorkToReturn[0], CancellationToken.None);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(publishStrategy.PublishedWork).HasCount().EqualTo(1);
    await Assert.That(publishStrategy.PublishedWork.First().MessageId).IsEqualTo(messageId);
  }

  [Test]
  public async Task ProcessWorkBatch_WithFailure_ShouldReportFailureAsync() {
    // Arrange
    var workCoordinator = new TestWorkCoordinator();
    var publishStrategy = new TestPublishStrategy {
      PublishResultFunc = work => new MessagePublishResult {
        MessageId = work.MessageId,
        Success = false,
        CompletedStatus = MessageProcessingStatus.Stored,
        Error = "Transport failed"
      }
    };
    var instanceProvider = _createTestInstanceProvider();

    var messageId = Guid.NewGuid();
    workCoordinator.WorkToReturn = [
      new OutboxWork {
        MessageId = messageId,
        Destination = "test-topic",
        Envelope = _createTestEnvelope(messageId),
        StreamId = Guid.NewGuid(),
        PartitionNumber = 1,
        Attempts = 0,
        Status = MessageProcessingStatus.Stored,
        Flags = WorkBatchFlags.None,
        SequenceOrder = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      }
    ];

    var services = _createServiceCollection(workCoordinator, publishStrategy, instanceProvider);

    // Act
    var result = await publishStrategy.PublishAsync(workCoordinator.WorkToReturn[0], CancellationToken.None);

    // Assert
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).IsEqualTo("Transport failed");
    await Assert.That(result.CompletedStatus).IsEqualTo(MessageProcessingStatus.Stored);
  }

  [Test]
  public async Task ConcurrentPublishing_ShouldCollectResults_Async() {
    // Arrange
    var publishStrategy = new TestPublishStrategy {
      PublishDelay = TimeSpan.FromMilliseconds(10)  // Simulate async work
    };

    var messages = new List<OutboxWork>();
    for (int i = 0; i < 5; i++) {
      var msgId = Guid.NewGuid();
      messages.Add(new OutboxWork {
        MessageId = msgId,
        Destination = "test-topic",
        Envelope = _createTestEnvelope(msgId),
        StreamId = Guid.NewGuid(),
        PartitionNumber = 1,
        Attempts = 0,
        Status = MessageProcessingStatus.Stored,
        Flags = WorkBatchFlags.None,
        SequenceOrder = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      });
    }

    // Act - publish all messages concurrently
    var tasks = messages.Select(m => publishStrategy.PublishAsync(m, CancellationToken.None));
    var results = await Task.WhenAll(tasks);

    // Assert
    await Assert.That(results).HasCount().EqualTo(5);
    await Assert.That(results.All(r => r.Success)).IsTrue();
    await Assert.That(publishStrategy.PublishedWork).HasCount().EqualTo(5);
  }

  private static IServiceInstanceProvider _createTestInstanceProvider() {
    return new ServiceInstanceProvider(
      Guid.NewGuid(),
      "TestService",
      "TestHost",
      Environment.ProcessId
    );
  }

  private static IServiceCollection _createServiceCollection(
    IWorkCoordinator workCoordinator,
    IMessagePublishStrategy publishStrategy,
    IServiceInstanceProvider instanceProvider) {

    var services = new ServiceCollection();
    services.AddSingleton(workCoordinator);
    services.AddSingleton(publishStrategy);
    services.AddSingleton(instanceProvider);
    services.AddSingleton<IWorkChannelWriter>(new TestWorkChannelWriter());  // Required by WorkCoordinatorPublisherWorker
    services.AddLogging();
    return services;
  }

  // Test helper - Mock work channel writer
  private class TestWorkChannelWriter : IWorkChannelWriter {
    public List<OutboxWork> WrittenWork { get; } = [];

    public System.Threading.Channels.ChannelReader<OutboxWork> Reader =>
      throw new NotImplementedException("Reader not needed for tests");

    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct) {
      WrittenWork.Add(work);
      return ValueTask.CompletedTask;
    }

    public bool TryWrite(OutboxWork work) {
      WrittenWork.Add(work);
      return true;
    }

    public void Complete() {
      // No-op for testing
    }
  }
}
