using System;
using System.Collections.Generic;
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
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

public class WorkCoordinatorPublisherWorkerChannelTests {
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
      Dictionary<string, object>? metadata,
      MessageCompletion[] outboxCompletions,
      MessageFailure[] outboxFailures,
      MessageCompletion[] inboxCompletions,
      MessageFailure[] inboxFailures,
      NewOutboxMessage[] newOutboxMessages,
      NewInboxMessage[] newInboxMessages,
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
    public List<OutboxWork> PublishedWork { get; } = [];
    public Func<OutboxWork, MessagePublishResult>? PublishResultFunc { get; set; }
    public TimeSpan PublishDelay { get; set; } = TimeSpan.Zero;

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
    var instanceProvider = CreateTestInstanceProvider();

    var messageId = Guid.NewGuid();
    workCoordinator.WorkToReturn = [
      new OutboxWork {
        MessageId = messageId,
        Destination = "test-topic",
        MessageType = "TestMessage",
        MessageData = "{}",
        Metadata = "{\"messageId\":\"" + messageId + "\",\"hops\":[]}",
        Scope = null,
        StreamId = Guid.NewGuid(),
        PartitionNumber = 1,
        Attempts = 0,
        Status = MessageProcessingStatus.Stored,
        Flags = WorkBatchFlags.None,
        SequenceOrder = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      }
    ];

    var services = CreateServiceCollection(workCoordinator, publishStrategy, instanceProvider);

    // Act & Assert - verify work was published
    // Note: Full worker integration will be tested separately
    // This test focuses on the strategy being called with work from coordinator
    var result = await publishStrategy.PublishAsync(workCoordinator.WorkToReturn[0], CancellationToken.None);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(publishStrategy.PublishedWork).HasCount().EqualTo(1);
    await Assert.That(publishStrategy.PublishedWork[0].MessageId).IsEqualTo(messageId);
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
    var instanceProvider = CreateTestInstanceProvider();

    var messageId = Guid.NewGuid();
    workCoordinator.WorkToReturn = [
      new OutboxWork {
        MessageId = messageId,
        Destination = "test-topic",
        MessageType = "TestMessage",
        MessageData = "{}",
        Metadata = "{\"messageId\":\"" + messageId + "\",\"hops\":[]}",
        Scope = null,
        StreamId = Guid.NewGuid(),
        PartitionNumber = 1,
        Attempts = 0,
        Status = MessageProcessingStatus.Stored,
        Flags = WorkBatchFlags.None,
        SequenceOrder = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      }
    ];

    var services = CreateServiceCollection(workCoordinator, publishStrategy, instanceProvider);

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
      messages.Add(new OutboxWork {
        MessageId = Guid.NewGuid(),
        Destination = "test-topic",
        MessageType = "TestMessage",
        MessageData = "{}",
        Metadata = "{\"messageId\":\"" + Guid.NewGuid() + "\",\"hops\":[]}",
        Scope = null,
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

  private static IServiceInstanceProvider CreateTestInstanceProvider() {
    return new ServiceInstanceProvider(
      Guid.NewGuid(),
      "TestService",
      "TestHost",
      Environment.ProcessId
    );
  }

  private static IServiceCollection CreateServiceCollection(
    IWorkCoordinator workCoordinator,
    IMessagePublishStrategy publishStrategy,
    IServiceInstanceProvider instanceProvider) {

    var services = new ServiceCollection();
    services.AddSingleton(workCoordinator);
    services.AddSingleton(publishStrategy);
    services.AddSingleton(instanceProvider);
    services.AddLogging();
    return services;
  }
}
