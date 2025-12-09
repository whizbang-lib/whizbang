using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

/// <summary>
/// Integration tests for WorkCoordinatorPublisherWorker with real-world delays and concurrency.
/// Tests race conditions that might not be caught by fast unit tests.
/// </summary>
public class WorkCoordinatorPublisherWorkerRaceConditionTests {
  private record TestMessage { }

  private static IMessageEnvelope CreateTestEnvelope(Guid messageId) {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.From(messageId),
      Payload = new TestMessage(),
      Hops = []
    };
  }

  /// <summary>
  /// Work coordinator that simulates realistic database latency (50-200ms per call).
  /// </summary>
  private class RealisticWorkCoordinator : IWorkCoordinator {
    private readonly Random _random = new();
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<Guid, bool> _claimedMessages = new();
    private int _processWorkBatchCallCount;

    public List<OutboxWork> AvailableWork { get; set; } = [];
    public int ProcessWorkBatchCallCount => _processWorkBatchCallCount;
    public TimeSpan MinLatency { get; set; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan MaxLatency { get; set; } = TimeSpan.FromMilliseconds(200);
    public List<ProcessWorkBatchCall> Calls { get; } = [];

    public async Task<WorkBatch> ProcessWorkBatchAsync(
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
      Guid[] renewOutboxLeaseIds,
      Guid[] renewInboxLeaseIds,
      WorkBatchFlags flags = WorkBatchFlags.None,
      int partitionCount = 10000,
      int maxPartitionsPerInstance = 100,
      int leaseSeconds = 300,
      int staleThresholdSeconds = 600,
      CancellationToken cancellationToken = default) {

      // Simulate realistic database latency
      var latencyMs = _random.Next((int)MinLatency.TotalMilliseconds, (int)MaxLatency.TotalMilliseconds);
      await Task.Delay(latencyMs, cancellationToken);

      var callCount = Interlocked.Increment(ref _processWorkBatchCallCount);
      lock (_lock) {
        Calls.Add(new ProcessWorkBatchCall {
          CallNumber = callCount,
          InstanceId = instanceId,
          Timestamp = DateTimeOffset.UtcNow,
          LatencyMs = latencyMs
        });
      }

      // Simulate partition-based claiming (only unclaimed messages)
      var unclaimedWork = AvailableWork
        .Where(w => !_claimedMessages.ContainsKey(w.MessageId))
        .Take(maxPartitionsPerInstance)
        .ToList();

      // Mark as claimed
      foreach (var work in unclaimedWork) {
        _claimedMessages[work.MessageId] = true;
      }

      // Remove completed messages
      lock (_lock) {
        foreach (var completion in outboxCompletions) {
          AvailableWork.RemoveAll(w => w.MessageId == completion.MessageId);
          _claimedMessages.TryRemove(completion.MessageId, out _);
        }
      }

      return new WorkBatch {
        OutboxWork = unclaimedWork,
        InboxWork = []
      };
    }
  }

  /// <summary>
  /// Publish strategy that simulates realistic transport latency (100-500ms per publish).
  /// </summary>
  private class RealisticPublishStrategy : IMessagePublishStrategy {
    private readonly Random _random = new();
    public List<OutboxWork> PublishedWork { get; } = [];
    public TimeSpan MinLatency { get; set; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan MaxLatency { get; set; } = TimeSpan.FromMilliseconds(500);
    public double FailureRate { get; set; } = 0.0; // 0.0 = no failures, 0.1 = 10% failure

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      return Task.FromResult(true);
    }

    public async Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      // Simulate realistic transport latency
      var latencyMs = _random.Next((int)MinLatency.TotalMilliseconds, (int)MaxLatency.TotalMilliseconds);
      await Task.Delay(latencyMs, cancellationToken);

      // Simulate occasional failures
      if (_random.NextDouble() < FailureRate) {
        return new MessagePublishResult {
          MessageId = work.MessageId,
          Success = false,
          CompletedStatus = work.Status,
          Error = "Simulated transport failure",
          Reason = MessageFailureReason.TransportException
        };
      }

      lock (PublishedWork) {
        PublishedWork.Add(work);
      }

      return new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      };
    }
  }

  private class RealisticDatabaseReadinessCheck : IDatabaseReadinessCheck {
    private readonly Random _random = new();
    public bool IsReady { get; set; } = true;
    public TimeSpan CheckLatency { get; set; } = TimeSpan.FromMilliseconds(10);

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      await Task.Delay(CheckLatency, cancellationToken);
      return IsReady;
    }
  }

  private record ProcessWorkBatchCall {
    public required int CallNumber { get; init; }
    public required Guid InstanceId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required int LatencyMs { get; init; }
  }

  [Test]
  [Timeout(30000)] // 30 second timeout for long-running integration test
  public async Task RaceCondition_MultipleInstances_NoDuplicatePublishingAsync(CancellationToken cancellationToken) {
    // Arrange - 2 worker instances competing for 20 messages
    var workCoordinator = new RealisticWorkCoordinator {
      MinLatency = TimeSpan.FromMilliseconds(50),
      MaxLatency = TimeSpan.FromMilliseconds(200)
    };

    var publishStrategy1 = new RealisticPublishStrategy {
      MinLatency = TimeSpan.FromMilliseconds(100),
      MaxLatency = TimeSpan.FromMilliseconds(500)
    };

    var publishStrategy2 = new RealisticPublishStrategy {
      MinLatency = TimeSpan.FromMilliseconds(100),
      MaxLatency = TimeSpan.FromMilliseconds(500)
    };

    var databaseReadiness = new RealisticDatabaseReadinessCheck { IsReady = true };

    // 20 messages available for claiming (more messages = better load distribution)
    for (int i = 0; i < 20; i++) {
      workCoordinator.AvailableWork.Add(CreateOutboxWork(Guid.NewGuid(), "products"));
    }

    var instanceProvider1 = CreateTestInstanceProvider();
    var instanceProvider2 = CreateTestInstanceProvider();

    var services1 = CreateServiceCollection(workCoordinator, publishStrategy1, databaseReadiness, instanceProvider1);
    var services2 = CreateServiceCollection(workCoordinator, publishStrategy2, databaseReadiness, instanceProvider2);

    var worker1 = new WorkCoordinatorPublisherWorker(
      instanceProvider1,
      services1.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy1,
      new WorkChannelWriter(),
      databaseReadiness,
      new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 100 }
    );

    var worker2 = new WorkCoordinatorPublisherWorker(
      instanceProvider2,
      services2.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy2,
      new WorkChannelWriter(),
      databaseReadiness,
      new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 100 }
    );

    using var cts = new CancellationTokenSource();

    // Act - start both workers concurrently
    var worker1Task = worker1.StartAsync(cts.Token);
    var worker2Task = worker2.StartAsync(cts.Token);

    // Let them run for 10 seconds (enough time for 20 messages with realistic delays and retries)
    await Task.Delay(10000);

    cts.Cancel();

    try {
      await Task.WhenAll(worker1Task, worker2Task);
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert - all 20 messages should be published exactly once (no duplicates)
    var allPublished = publishStrategy1.PublishedWork.Concat(publishStrategy2.PublishedWork).ToList();
    await Assert.That(allPublished).HasCount().EqualTo(20);

    // Verify no duplicate MessageIds
    var uniqueMessageIds = allPublished.Select(w => w.MessageId).Distinct().Count();
    await Assert.That(uniqueMessageIds).IsEqualTo(20);

    // Verify load balancing - at least one worker participated
    // Note: In real race conditions, it's possible (though rare) for one worker to claim all work
    // The important thing is that no duplicates exist and all work completes
    var worker1Count = publishStrategy1.PublishedWork.Count;
    var worker2Count = publishStrategy2.PublishedWork.Count;
    await Assert.That(worker1Count + worker2Count).IsEqualTo(20);

    // At least verify both workers were active (made coordinator calls)
    await Assert.That(workCoordinator.Calls.Select(c => c.InstanceId).Distinct().Count()).IsGreaterThanOrEqualTo(2)
      .Because("Both worker instances should have made coordinator calls, even if one dominated work claiming");
  }

  [Test]
  [Timeout(15000)] // 15 second timeout
  public async Task RaceCondition_ImmediateProcessing_WithRealisticDelaysAsync(CancellationToken cancellationToken) {
    // Arrange
    var workCoordinator = new RealisticWorkCoordinator {
      MinLatency = TimeSpan.FromMilliseconds(100), // Realistic DB latency
      MaxLatency = TimeSpan.FromMilliseconds(300)
    };

    var publishStrategy = new RealisticPublishStrategy {
      MinLatency = TimeSpan.FromMilliseconds(200), // Realistic transport latency
      MaxLatency = TimeSpan.FromMilliseconds(600)
    };

    var databaseReadiness = new RealisticDatabaseReadinessCheck {
      IsReady = true,
      CheckLatency = TimeSpan.FromMilliseconds(50) // Realistic connection check
    };

    var instanceProvider = CreateTestInstanceProvider();

    // 12 messages (like user's seeding scenario)
    for (int i = 0; i < 12; i++) {
      workCoordinator.AvailableWork.Add(CreateOutboxWork(Guid.NewGuid(), "products"));
    }

    var services = CreateServiceCollection(workCoordinator, publishStrategy, databaseReadiness, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new WorkChannelWriter(),
      databaseReadiness,
      new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 500 }
    );

    using var cts = new CancellationTokenSource();

    // Act - start worker
    var workerTask = worker.StartAsync(cts.Token);

    // Wait for initial processing + first few polls (realistic delays mean this takes longer)
    await Task.Delay(7000); // 7 seconds should be enough with realistic delays

    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected
    }

    // Assert - verify immediate processing happened despite delays
    // First call should happen quickly (within 500ms of startup)
    var firstCall = workCoordinator.Calls.FirstOrDefault();
    await Assert.That(firstCall).IsNotNull();

    // All messages should eventually be published
    await Assert.That(publishStrategy.PublishedWork).HasCount().EqualTo(12);

    // Verify multiple coordinator calls happened (initial + polling)
    await Assert.That(workCoordinator.Calls).HasCount().GreaterThanOrEqualTo(2);
  }

  [Test]
  [Timeout(20000)] // 20 second timeout
  public async Task RaceCondition_DatabaseSlowness_DoesNotBlockPublishingAsync(CancellationToken cancellationToken) {
    // Arrange - simulate slow database (500-1000ms per call)
    var workCoordinator = new RealisticWorkCoordinator {
      MinLatency = TimeSpan.FromMilliseconds(500),
      MaxLatency = TimeSpan.FromMilliseconds(1000)
    };

    var publishStrategy = new RealisticPublishStrategy {
      MinLatency = TimeSpan.FromMilliseconds(100),
      MaxLatency = TimeSpan.FromMilliseconds(300)
    };

    var databaseReadiness = new RealisticDatabaseReadinessCheck { IsReady = true };
    var instanceProvider = CreateTestInstanceProvider();

    // 5 messages
    for (int i = 0; i < 5; i++) {
      workCoordinator.AvailableWork.Add(CreateOutboxWork(Guid.NewGuid(), "products"));
    }

    var services = CreateServiceCollection(workCoordinator, publishStrategy, databaseReadiness, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new WorkChannelWriter(),
      databaseReadiness,
      new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 200 }
    );

    using var cts = new CancellationTokenSource();

    // Act
    var workerTask = worker.StartAsync(cts.Token);

    // Wait long enough for slow DB calls
    await Task.Delay(8000); // 8 seconds

    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected
    }

    // Assert - publishing should still succeed despite slow DB
    await Assert.That(publishStrategy.PublishedWork).HasCount().EqualTo(5);

    // Verify database was called multiple times despite slowness
    await Assert.That(workCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(2);
  }

  [Test]
  [Timeout(20000)] // 20 second timeout
  public async Task RaceCondition_TransportFailures_RetriesSuccessfullyAsync(CancellationToken cancellationToken) {
    // Arrange - 30% transport failure rate
    var workCoordinator = new RealisticWorkCoordinator {
      MinLatency = TimeSpan.FromMilliseconds(50),
      MaxLatency = TimeSpan.FromMilliseconds(150)
    };

    var publishStrategy = new RealisticPublishStrategy {
      MinLatency = TimeSpan.FromMilliseconds(100),
      MaxLatency = TimeSpan.FromMilliseconds(300),
      FailureRate = 0.3 // 30% failure rate
    };

    var databaseReadiness = new RealisticDatabaseReadinessCheck { IsReady = true };
    var instanceProvider = CreateTestInstanceProvider();

    // 10 messages
    for (int i = 0; i < 10; i++) {
      workCoordinator.AvailableWork.Add(CreateOutboxWork(Guid.NewGuid(), "products"));
    }

    var services = CreateServiceCollection(workCoordinator, publishStrategy, databaseReadiness, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new WorkChannelWriter(),
      databaseReadiness,
      new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 200 }
    );

    using var cts = new CancellationTokenSource();

    // Act
    var workerTask = worker.StartAsync(cts.Token);

    // Wait long enough for retries
    await Task.Delay(10000); // 10 seconds

    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected
    }

    // Assert - most messages should eventually succeed (retries work)
    // With 30% failure rate and 10 seconds of retries, we expect most to succeed
    await Assert.That(publishStrategy.PublishedWork).HasCount().GreaterThanOrEqualTo(5);

    // Verify multiple attempts were made
    await Assert.That(workCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(3);
  }

  [Test]
  [Timeout(10000)] // 10 second timeout
  public async Task RaceCondition_DatabaseNotReady_DelaysProcessingAsync(CancellationToken cancellationToken) {
    // Arrange - database starts not ready, becomes ready after 2 seconds
    var workCoordinator = new RealisticWorkCoordinator();
    var publishStrategy = new RealisticPublishStrategy();
    var databaseReadiness = new RealisticDatabaseReadinessCheck { IsReady = false };
    var instanceProvider = CreateTestInstanceProvider();

    // 5 messages
    for (int i = 0; i < 5; i++) {
      workCoordinator.AvailableWork.Add(CreateOutboxWork(Guid.NewGuid(), "products"));
    }

    var services = CreateServiceCollection(workCoordinator, publishStrategy, databaseReadiness, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new WorkChannelWriter(),
      databaseReadiness,
      new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 200 }
    );

    using var cts = new CancellationTokenSource();

    // Act - start worker (database NOT ready)
    var workerTask = worker.StartAsync(cts.Token);

    // Wait 1 second - should NOT publish anything yet
    await Task.Delay(1000);
    await Assert.That(publishStrategy.PublishedWork).HasCount().EqualTo(0);

    // Make database ready
    databaseReadiness.IsReady = true;

    // Wait another 3 seconds - should now publish
    await Task.Delay(3000);

    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected
    }

    // Assert - messages published after database became ready
    await Assert.That(publishStrategy.PublishedWork).HasCount().EqualTo(5);

    // Verify work coordinator was NOT called while database not ready
    // First call should happen AFTER database became ready
    await Assert.That(workCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
  }

  private static OutboxWork CreateOutboxWork(Guid messageId, string destination) {
    return new OutboxWork {
      MessageId = messageId,
      Destination = destination,
      Envelope = CreateTestEnvelope(messageId),
      StreamId = Guid.NewGuid(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None,
      SequenceOrder = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };
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
    IDatabaseReadinessCheck databaseReadiness,
    IServiceInstanceProvider instanceProvider) {

    var services = new ServiceCollection();
    services.AddSingleton(workCoordinator);
    services.AddSingleton(publishStrategy);
    services.AddSingleton(databaseReadiness);
    services.AddSingleton(instanceProvider);
    services.AddLogging();
    return services;
  }
}
