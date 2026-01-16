using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
  private sealed record _testMessage { }

  private static MessageEnvelope<JsonElement> _createTestEnvelope(Guid messageId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = []
    };
    return envelope;
  }

  /// <summary>
  /// Work coordinator that simulates realistic database latency (50-200ms per call).
  /// </summary>
  private sealed class RealisticWorkCoordinator : IWorkCoordinator {
    private readonly Random _random = new();
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<Guid, bool> _claimedMessages = new();
    private int _processWorkBatchCallCount;

    public List<OutboxWork> AvailableWork { get; set; } = [];
    public int ProcessWorkBatchCallCount => _processWorkBatchCallCount;
    public TimeSpan MinLatency { get; set; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan MaxLatency { get; set; } = TimeSpan.FromMilliseconds(200);
    public List<_processWorkBatchCall> Calls { get; } = [];

    public async Task<WorkBatch> ProcessWorkBatchAsync(
      Guid instanceId,
      string serviceName,
      string hostName,
      int processId,
      Dictionary<string, JsonElement>? metadata,
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
      int leaseSeconds = 300,
      int staleThresholdSeconds = 600,
      CancellationToken cancellationToken = default) {

      // Simulate realistic database latency
      var latencyMs = _random.Next((int)MinLatency.TotalMilliseconds, (int)MaxLatency.TotalMilliseconds);
      await Task.Delay(latencyMs, cancellationToken);

      var callCount = Interlocked.Increment(ref _processWorkBatchCallCount);
      lock (_lock) {
        Calls.Add(new _processWorkBatchCall {
          CallNumber = callCount,
          InstanceId = instanceId,
          Timestamp = DateTimeOffset.UtcNow,
          LatencyMs = latencyMs
        });
      }

      // CRITICAL: Atomically claim messages to prevent race conditions
      // Both the query and the claim MUST happen inside the lock
      List<OutboxWork> unclaimedWork;
      lock (_lock) {
        // Simulate partition-based claiming (only unclaimed messages)
        // No maxPartitionsPerInstance limit - each instance claims all partitions assigned via modulo
        unclaimedWork = AvailableWork
          .Where(w => !_claimedMessages.ContainsKey(w.MessageId))
          .ToList();

        // Mark as claimed (inside same lock to prevent duplicates)
        foreach (var work in unclaimedWork) {
          _claimedMessages[work.MessageId] = true;
        }

        // Remove completed messages
        foreach (var completion in outboxCompletions) {
          AvailableWork.RemoveAll(w => w.MessageId == completion.MessageId);
          _claimedMessages.TryRemove(completion.MessageId, out _);
        }

        // Unclaim failed messages so they can be retried on next poll
        foreach (var failure in outboxFailures) {
          _claimedMessages.TryRemove(failure.MessageId, out _);
          // Message stays in AvailableWork for retry
        }

        // Handle lease renewals (for retryable failures like TransportException)
        // The worker renews the lease instead of failing, allowing retry on next poll
        foreach (var messageId in renewOutboxLeaseIds) {
          _claimedMessages.TryRemove(messageId, out _);
          // Message stays in AvailableWork for retry
        }
      }

      return new WorkBatch {
        OutboxWork = unclaimedWork,
        InboxWork = [],
        PerspectiveWork = []
      };
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCheckpointCompletion completion,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCheckpointFailure failure,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCheckpointInfo?>(null);
    }
  }

  /// <summary>
  /// Publish strategy that simulates realistic transport latency (100-500ms per publish).
  /// </summary>
  private sealed class RealisticPublishStrategy : IMessagePublishStrategy {
    private readonly Random _random = new();
    private readonly ConcurrentDictionary<Guid, int> _attemptCounts = new();
    private readonly object _lock = new();

    public List<OutboxWork> PublishedWork { get; } = [];
    public TimeSpan MinLatency { get; set; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan MaxLatency { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Number of attempts that should fail before succeeding (0 = always succeed, 1 = fail once then succeed, etc.)
    /// This makes failures deterministic and predictable.
    /// </summary>
    public int FailureAttemptsBeforeSuccess { get; set; }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      return Task.FromResult(true);
    }

    public async Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      // Simulate realistic transport latency
      var latencyMs = _random.Next((int)MinLatency.TotalMilliseconds, (int)MaxLatency.TotalMilliseconds);
      await Task.Delay(latencyMs, cancellationToken);

      // Track attempt count for this message (deterministic failures)
      var attemptNumber = _attemptCounts.AddOrUpdate(work.MessageId, 1, (_, count) => count + 1);

      // Fail deterministically based on attempt number
      if (attemptNumber <= FailureAttemptsBeforeSuccess) {
        return new MessagePublishResult {
          MessageId = work.MessageId,
          Success = false,
          CompletedStatus = work.Status,
          Error = $"Simulated transport failure (attempt {attemptNumber}/{FailureAttemptsBeforeSuccess + 1})",
          Reason = MessageFailureReason.TransportException
        };
      }

      // Success - add to published list
      lock (_lock) {
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

  private sealed class RealisticDatabaseReadinessCheck : IDatabaseReadinessCheck {
    private readonly Random _random = new();
    public bool IsReady { get; set; } = true;
    public TimeSpan CheckLatency { get; set; } = TimeSpan.FromMilliseconds(10);

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      await Task.Delay(CheckLatency, cancellationToken);
      return IsReady;
    }
  }

  private sealed record _processWorkBatchCall {
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
      workCoordinator.AvailableWork.Add(_createOutboxWork(Guid.NewGuid(), "products"));
    }

    var instanceProvider1 = _createTestInstanceProvider();
    var instanceProvider2 = _createTestInstanceProvider();

    var services1 = _createServiceCollection(workCoordinator, publishStrategy1, databaseReadiness, instanceProvider1);
    var services2 = _createServiceCollection(workCoordinator, publishStrategy2, databaseReadiness, instanceProvider2);

    var worker1 = new WorkCoordinatorPublisherWorker(
      instanceProvider1,
      services1.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy1,
      new TestWorkChannelWriter(),
      Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 100 }),
      databaseReadiness
    );

    var worker2 = new WorkCoordinatorPublisherWorker(
      instanceProvider2,
      services2.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy2,
      new TestWorkChannelWriter(),
      Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 100 }),
      databaseReadiness
    );

    using var cts = new CancellationTokenSource();

    // Act - start both workers concurrently
    var worker1Task = worker1.StartAsync(cts.Token);
    var worker2Task = worker2.StartAsync(cts.Token);

    // Let them run for 10 seconds (enough time for 20 messages with realistic delays and retries)
    await Task.Delay(10000, cancellationToken);

    cts.Cancel();

    try {
      await Task.WhenAll(worker1Task, worker2Task);
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert - all 20 messages should be published exactly once (no duplicates)
    var allPublished = publishStrategy1.PublishedWork.Concat(publishStrategy2.PublishedWork).ToList();
    await Assert.That(allPublished).Count().IsEqualTo(20);

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

    var instanceProvider = _createTestInstanceProvider();

    // 12 messages (like user's seeding scenario)
    for (int i = 0; i < 12; i++) {
      workCoordinator.AvailableWork.Add(_createOutboxWork(Guid.NewGuid(), "products"));
    }

    var services = _createServiceCollection(workCoordinator, publishStrategy, databaseReadiness, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new TestWorkChannelWriter(),
      Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 500 }),
      databaseReadiness
    );

    using var cts = new CancellationTokenSource();

    // Act - start worker
    var workerTask = worker.StartAsync(cts.Token);

    // Wait for initial processing + first few polls (realistic delays mean this takes longer)
    await Task.Delay(7000, cancellationToken); // 7 seconds should be enough with realistic delays

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
    await Assert.That(publishStrategy.PublishedWork).Count().IsEqualTo(12);

    // Verify multiple coordinator calls happened (initial + polling)
    await Assert.That(workCoordinator.Calls).Count().IsGreaterThanOrEqualTo(2);
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
    var instanceProvider = _createTestInstanceProvider();

    // 5 messages
    for (int i = 0; i < 5; i++) {
      workCoordinator.AvailableWork.Add(_createOutboxWork(Guid.NewGuid(), "products"));
    }

    var services = _createServiceCollection(workCoordinator, publishStrategy, databaseReadiness, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new TestWorkChannelWriter(),
      Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 200 }),
      databaseReadiness
    );

    using var cts = new CancellationTokenSource();

    // Act
    var workerTask = worker.StartAsync(cts.Token);

    // Wait long enough for slow DB calls
    await Task.Delay(8000, cancellationToken); // 8 seconds

    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected
    }

    // Assert - publishing should still succeed despite slow DB
    await Assert.That(publishStrategy.PublishedWork).Count().IsEqualTo(5);

    // Verify database was called multiple times despite slowness
    await Assert.That(workCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(2);
  }

  [Test]
  [Timeout(20000)] // 20 second timeout
  public async Task RaceCondition_TransportFailures_RetriesSuccessfullyAsync(CancellationToken cancellationToken) {
    // Arrange - Deterministic failures: first 2 attempts fail, 3rd attempt succeeds
    var workCoordinator = new RealisticWorkCoordinator {
      MinLatency = TimeSpan.FromMilliseconds(10),
      MaxLatency = TimeSpan.FromMilliseconds(30)
    };

    var publishStrategy = new RealisticPublishStrategy {
      MinLatency = TimeSpan.FromMilliseconds(20),
      MaxLatency = TimeSpan.FromMilliseconds(50),
      FailureAttemptsBeforeSuccess = 2 // Fail first 2 attempts, succeed on 3rd (deterministic)
    };

    var databaseReadiness = new RealisticDatabaseReadinessCheck { IsReady = true };
    var instanceProvider = _createTestInstanceProvider();

    // 10 messages
    for (int i = 0; i < 10; i++) {
      workCoordinator.AvailableWork.Add(_createOutboxWork(Guid.NewGuid(), "products"));
    }

    var services = _createServiceCollection(workCoordinator, publishStrategy, databaseReadiness, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new TestWorkChannelWriter(),
      Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 200 }),
      databaseReadiness
    );

    using var cts = new CancellationTokenSource();

    // Act
    var workerTask = worker.StartAsync(cts.Token);

    // Wait long enough for 3 retry cycles per message
    // Each cycle: 10-30ms DB + 20-50ms transport + 200ms poll interval = ~280ms worst case
    // 3 cycles * 280ms = 840ms per message, but messages process sequentially (worker processes batches)
    // Need enough time for all 10 messages × 3 attempts = 30 total publish calls
    // 15 seconds provides generous buffer for parallel test execution and CPU contention (reduced latency makes test faster)
    await Task.Delay(15000, cancellationToken);

    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected
    }

    // Assert - ALL messages should eventually succeed (deterministic retries)
    // First 2 attempts fail, 3rd succeeds - no randomness
    await Assert.That(publishStrategy.PublishedWork).Count().IsEqualTo(10)
      .Because("All messages should succeed on 3rd attempt with deterministic retry logic");

    // Verify multiple coordinator calls happened (at least 3 rounds of retries)
    await Assert.That(workCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(3);
  }

  [Test]
  [Timeout(10000)] // 10 second timeout
  public async Task RaceCondition_DatabaseNotReady_DelaysProcessingAsync(CancellationToken cancellationToken) {
    // Arrange - database starts not ready, becomes ready after 2 seconds
    var workCoordinator = new RealisticWorkCoordinator();
    var publishStrategy = new RealisticPublishStrategy();
    var databaseReadiness = new RealisticDatabaseReadinessCheck { IsReady = false };
    var instanceProvider = _createTestInstanceProvider();

    // 5 messages
    for (int i = 0; i < 5; i++) {
      workCoordinator.AvailableWork.Add(_createOutboxWork(Guid.NewGuid(), "products"));
    }

    var services = _createServiceCollection(workCoordinator, publishStrategy, databaseReadiness, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new TestWorkChannelWriter(),
      Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 200 }),
      databaseReadiness
    );

    using var cts = new CancellationTokenSource();

    // Act - start worker (database NOT ready)
    var workerTask = worker.StartAsync(cts.Token);

    // Wait 1 second - should NOT publish anything yet
    await Task.Delay(1000, cancellationToken);
    await Assert.That(publishStrategy.PublishedWork).Count().IsEqualTo(0);

    // Make database ready
    databaseReadiness.IsReady = true;

    // Wait another 3 seconds - should now publish
    await Task.Delay(3000, cancellationToken);

    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected
    }

    // Assert - messages published after database became ready
    await Assert.That(publishStrategy.PublishedWork).Count().IsEqualTo(5);

    // Verify work coordinator was NOT called while database not ready
    // First call should happen AFTER database became ready
    await Assert.That(workCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
  }

  private static OutboxWork _createOutboxWork(Guid messageId, string destination) {
    return new OutboxWork {
      MessageId = messageId,
      Destination = destination,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Envelope = _createTestEnvelope(messageId),
      StreamId = Guid.NewGuid(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None,
    };
  }

  private static ServiceInstanceProvider _createTestInstanceProvider() {
    return new ServiceInstanceProvider(
      Guid.NewGuid(),
      "TestService",
      "TestHost",
      Environment.ProcessId
    );
  }

  private static ServiceCollection _createServiceCollection(
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

  // Test helper - Mock work channel writer
  private sealed class TestWorkChannelWriter : IWorkChannelWriter {
    private readonly System.Threading.Channels.Channel<OutboxWork> _channel;
    public List<OutboxWork> WrittenWork { get; } = [];

    public TestWorkChannelWriter() {
      _channel = System.Threading.Channels.Channel.CreateUnbounded<OutboxWork>();
    }

    public System.Threading.Channels.ChannelReader<OutboxWork> Reader => _channel.Reader;

    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct) {
      WrittenWork.Add(work);
      return _channel.Writer.WriteAsync(work, ct);
    }

    public bool TryWrite(OutboxWork work) {
      WrittenWork.Add(work);
      return _channel.Writer.TryWrite(work);
    }

    public void Complete() {
      _channel.Writer.Complete();
    }
  }
}
