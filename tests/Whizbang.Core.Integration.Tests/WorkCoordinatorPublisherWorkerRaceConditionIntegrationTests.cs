using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Core.Dispatch;

namespace Whizbang.Core.Integration.Tests;

/// <summary>
/// Integration tests for WorkCoordinatorPublisherWorker with real-world delays and concurrency.
/// Tests race conditions that might not be caught by fast unit tests.
/// Uses proper synchronization patterns (TaskCompletionSource) instead of arbitrary delays.
/// </summary>
[Category("Integration")]
[NotInParallel("WorkCoordinatorRaceCondition")]
public class WorkCoordinatorPublisherWorkerRaceConditionIntegrationTests {
  private sealed record _testMessage { }

  private static MessageEnvelope<JsonElement> _createTestEnvelope(Guid messageId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    return envelope;
  }

  /// <summary>
  /// Work coordinator that simulates realistic database latency (50-200ms per call).
  /// Provides proper synchronization signals for deterministic testing.
  /// </summary>
  private sealed class SynchronizedWorkCoordinator : IWorkCoordinator {
    private readonly Random _random = new();
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<Guid, bool> _claimedMessages = new();
    private int _processWorkBatchCallCount;
    private readonly TaskCompletionSource _firstCallSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _allWorkCompletedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<OutboxWork> AvailableWork { get; set; } = [];
    public int ProcessWorkBatchCallCount => _processWorkBatchCallCount;
    public TimeSpan MinLatency { get; set; } = TimeSpan.FromMilliseconds(10);
    public TimeSpan MaxLatency { get; set; } = TimeSpan.FromMilliseconds(30);
    public List<_processWorkBatchCall> Calls { get; } = [];
    public int ExpectedCompletions { get; set; }

    /// <summary>
    /// Task that completes when ProcessWorkBatchAsync is called at least once.
    /// Use this instead of fixed delays to avoid flaky tests.
    /// </summary>
    public Task FirstCallReceived => _firstCallSignal.Task;

    /// <summary>
    /// Task that completes when all expected work items have been completed.
    /// </summary>
    public Task AllWorkCompleted => _allWorkCompletedSignal.Task;

    public async Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {

      // Simulate realistic database latency
      var latencyMs = _random.Next((int)MinLatency.TotalMilliseconds, (int)MaxLatency.TotalMilliseconds);
      await Task.Delay(latencyMs, cancellationToken);

      var callCount = Interlocked.Increment(ref _processWorkBatchCallCount);

      // Signal first call received
      _firstCallSignal.TrySetResult();

      lock (_lock) {
        Calls.Add(new _processWorkBatchCall {
          CallNumber = callCount,
          InstanceId = request.InstanceId,
          Timestamp = DateTimeOffset.UtcNow,
          LatencyMs = latencyMs
        });
      }

      // CRITICAL: Atomically claim messages to prevent race conditions
      List<OutboxWork> unclaimedWork;
      int completedCount;
      lock (_lock) {
        // Simulate partition-based claiming (only unclaimed messages)
        unclaimedWork = AvailableWork
          .Where(w => !_claimedMessages.ContainsKey(w.MessageId))
          .ToList();

        // Mark as claimed (inside same lock to prevent duplicates)
        foreach (var work in unclaimedWork) {
          _claimedMessages[work.MessageId] = true;
        }

        // Remove completed messages
        foreach (var completion in request.OutboxCompletions) {
          AvailableWork.RemoveAll(w => w.MessageId == completion.MessageId);
          _claimedMessages.TryRemove(completion.MessageId, out _);
        }

        // Unclaim failed messages so they can be retried on next poll
        foreach (var failure in request.OutboxFailures) {
          _claimedMessages.TryRemove(failure.MessageId, out _);
        }

        // Handle lease renewals
        foreach (var messageId in request.RenewOutboxLeaseIds) {
          _claimedMessages.TryRemove(messageId, out _);
        }

        completedCount = ExpectedCompletions - AvailableWork.Count;
      }

      // Signal when all work is completed
      if (ExpectedCompletions > 0 && completedCount >= ExpectedCompletions) {
        _allWorkCompletedSignal.TrySetResult();
      }

      return new WorkBatch {
        OutboxWork = unclaimedWork,
        InboxWork = [],
        PerspectiveWork = []
      };
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCursorInfo?>(null);
    }
  }

  /// <summary>
  /// Publish strategy that simulates realistic transport latency.
  /// Provides proper synchronization signals for deterministic testing.
  /// </summary>
  private sealed class SynchronizedPublishStrategy : IMessagePublishStrategy {
    private readonly Random _random = new();
    private readonly ConcurrentDictionary<Guid, int> _attemptCounts = new();
    private readonly object _lock = new();
    private int _publishedCount;
    private readonly TaskCompletionSource _allPublishedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentBag<OutboxWork> PublishedWork { get; } = [];
    public TimeSpan MinLatency { get; set; } = TimeSpan.FromMilliseconds(10);
    public TimeSpan MaxLatency { get; set; } = TimeSpan.FromMilliseconds(30);
    public int FailureAttemptsBeforeSuccess { get; set; }
    public int ExpectedPublishCount { get; set; }

    /// <summary>
    /// Task that completes when the expected number of messages have been published.
    /// </summary>
    public Task AllPublished => _allPublishedSignal.Task;

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      return Task.FromResult(true);
    }

    public async Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      // Simulate realistic transport latency
      var latencyMs = _random.Next((int)MinLatency.TotalMilliseconds, (int)MaxLatency.TotalMilliseconds);
      await Task.Delay(latencyMs, cancellationToken);

      // Track attempt count for deterministic failures
      var attemptNumber = _attemptCounts.AddOrUpdate(work.MessageId, 1, (_, count) => count + 1);

      if (attemptNumber <= FailureAttemptsBeforeSuccess) {
        return new MessagePublishResult {
          MessageId = work.MessageId,
          Success = false,
          CompletedStatus = work.Status,
          Error = $"Simulated transport failure (attempt {attemptNumber}/{FailureAttemptsBeforeSuccess + 1})",
          Reason = MessageFailureReason.TransportException
        };
      }

      // Success
      PublishedWork.Add(work);
      var currentCount = Interlocked.Increment(ref _publishedCount);

      // Signal when all expected messages are published
      if (ExpectedPublishCount > 0 && currentCount >= ExpectedPublishCount) {
        _allPublishedSignal.TrySetResult();
      }

      return new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      };
    }
  }

  private sealed class SynchronizedDatabaseReadinessCheck : IDatabaseReadinessCheck {
    private readonly TaskCompletionSource _becameReadySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _notReadyCheckReceivedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady { get; set; } = true;

    public Task BecameReady => _becameReadySignal.Task;

    /// <summary>
    /// Task that completes when IsReadyAsync has been called at least once while not ready.
    /// Use this instead of Task.Delay to verify the worker has polled during the not-ready state.
    /// </summary>
    public Task NotReadyCheckReceived => _notReadyCheckReceivedSignal.Task;

    public void SetReady() {
      IsReady = true;
      _becameReadySignal.TrySetResult();
    }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      if (!IsReady) {
        _notReadyCheckReceivedSignal.TrySetResult();
      }
      return Task.FromResult(IsReady);
    }
  }

  private sealed record _processWorkBatchCall {
    public required int CallNumber { get; init; }
    public required Guid InstanceId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required int LatencyMs { get; init; }
  }

  [Test]
  [Timeout(30000)] // Safety timeout only - test should complete much faster via signals
  public async Task RaceCondition_MultipleInstances_NoDuplicatePublishingAsync(CancellationToken cancellationToken) {
    // Arrange - 2 worker instances competing for 20 messages
    const int messageCount = 20;

    var workCoordinator = new SynchronizedWorkCoordinator {
      ExpectedCompletions = messageCount
    };

    var publishStrategy1 = new SynchronizedPublishStrategy();
    var publishStrategy2 = new SynchronizedPublishStrategy();

    var databaseReadiness = new SynchronizedDatabaseReadinessCheck { IsReady = true };

    for (int i = 0; i < messageCount; i++) {
      workCoordinator.AvailableWork.Add(_createOutboxWork(Guid.CreateVersion7(), "products"));
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
      Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 50 }),
      databaseReadiness
    );

    var worker2 = new WorkCoordinatorPublisherWorker(
      instanceProvider2,
      services2.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy2,
      new TestWorkChannelWriter(),
      Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 50 }),
      databaseReadiness
    );

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    // Act - start both workers concurrently
    var worker1Task = worker1.StartAsync(cts.Token);
    var worker2Task = worker2.StartAsync(cts.Token);

    // Wait for all work to complete (with safety timeout)
    var completionTask = workCoordinator.AllWorkCompleted;
    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(25), cancellationToken);
    await Task.WhenAny(completionTask, timeoutTask);

    cts.Cancel();

    try {
      await Task.WhenAll(worker1Task, worker2Task);
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert - all messages should be published exactly once (no duplicates)
    var allPublished = publishStrategy1.PublishedWork.Concat(publishStrategy2.PublishedWork).ToList();
    await Assert.That(allPublished).Count().IsEqualTo(messageCount);

    // Verify no duplicate MessageIds
    var uniqueMessageIds = allPublished.Select(w => w.MessageId).Distinct().Count();
    await Assert.That(uniqueMessageIds).IsEqualTo(messageCount);

    // Both workers should have made coordinator calls
    await Assert.That(workCoordinator.Calls.Select(c => c.InstanceId).Distinct().Count()).IsGreaterThanOrEqualTo(2)
      .Because("Both worker instances should have made coordinator calls");
  }

  [Test]
  [Timeout(15000)]
  public async Task RaceCondition_ImmediateProcessing_ProcessesWorkOnStartupAsync(CancellationToken cancellationToken) {
    // Arrange
    const int messageCount = 12;

    var workCoordinator = new SynchronizedWorkCoordinator {
      ExpectedCompletions = messageCount
    };

    var publishStrategy = new SynchronizedPublishStrategy {
      ExpectedPublishCount = messageCount
    };

    var databaseReadiness = new SynchronizedDatabaseReadinessCheck { IsReady = true };
    var instanceProvider = _createTestInstanceProvider();

    for (int i = 0; i < messageCount; i++) {
      workCoordinator.AvailableWork.Add(_createOutboxWork(Guid.CreateVersion7(), "products"));
    }

    var services = _createServiceCollection(workCoordinator, publishStrategy, databaseReadiness, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new TestWorkChannelWriter(),
      Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 100 }),
      databaseReadiness
    );

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    // Act - start worker
    var workerTask = worker.StartAsync(cts.Token);

    // Wait for first call (should happen immediately on startup)
    var firstCallTask = workCoordinator.FirstCallReceived;
    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    var firstCallResult = await Task.WhenAny(firstCallTask, timeoutTask);
    await Assert.That(firstCallResult).IsEqualTo(firstCallTask)
      .Because("First ProcessWorkBatchAsync call should happen immediately on startup");

    // Wait for all messages to be published
    var allPublishedTask = publishStrategy.AllPublished;
    var publishTimeoutTask = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
    await Task.WhenAny(allPublishedTask, publishTimeoutTask);

    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected
    }

    // Assert
    await Assert.That(publishStrategy.PublishedWork).Count().IsEqualTo(messageCount);
    await Assert.That(workCoordinator.Calls).Count().IsGreaterThanOrEqualTo(1);
  }

  [Test]
  [Timeout(20000)]
  public async Task RaceCondition_TransportFailures_RetriesSuccessfullyAsync(CancellationToken cancellationToken) {
    // Arrange - Deterministic failures: first 2 attempts fail, 3rd attempt succeeds
    const int messageCount = 10;

    var workCoordinator = new SynchronizedWorkCoordinator {
      ExpectedCompletions = messageCount
    };

    var publishStrategy = new SynchronizedPublishStrategy {
      FailureAttemptsBeforeSuccess = 2, // Fail first 2 attempts, succeed on 3rd
      ExpectedPublishCount = messageCount
    };

    var databaseReadiness = new SynchronizedDatabaseReadinessCheck { IsReady = true };
    var instanceProvider = _createTestInstanceProvider();

    for (int i = 0; i < messageCount; i++) {
      workCoordinator.AvailableWork.Add(_createOutboxWork(Guid.CreateVersion7(), "products"));
    }

    var services = _createServiceCollection(workCoordinator, publishStrategy, databaseReadiness, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new TestWorkChannelWriter(),
      Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 50 }),
      databaseReadiness
    );

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    // Act
    var workerTask = worker.StartAsync(cts.Token);

    // Wait for all messages to be successfully published (after retries)
    var allPublishedTask = publishStrategy.AllPublished;
    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
    await Task.WhenAny(allPublishedTask, timeoutTask);

    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected
    }

    // Assert - ALL messages should eventually succeed
    await Assert.That(publishStrategy.PublishedWork).Count().IsEqualTo(messageCount)
      .Because("All messages should succeed on 3rd attempt with deterministic retry logic");

    // Verify multiple coordinator calls happened (for retries)
    await Assert.That(workCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(3);
  }

  [Test]
  [Timeout(10000)]
  public async Task RaceCondition_DatabaseNotReady_DelaysProcessingAsync(CancellationToken cancellationToken) {
    // Arrange - database starts not ready
    const int messageCount = 5;

    var workCoordinator = new SynchronizedWorkCoordinator {
      ExpectedCompletions = messageCount
    };

    var publishStrategy = new SynchronizedPublishStrategy {
      ExpectedPublishCount = messageCount
    };

    var databaseReadiness = new SynchronizedDatabaseReadinessCheck { IsReady = false };
    var instanceProvider = _createTestInstanceProvider();

    for (int i = 0; i < messageCount; i++) {
      workCoordinator.AvailableWork.Add(_createOutboxWork(Guid.CreateVersion7(), "products"));
    }

    var services = _createServiceCollection(workCoordinator, publishStrategy, databaseReadiness, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new TestWorkChannelWriter(),
      Microsoft.Extensions.Options.Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 100 }),
      databaseReadiness
    );

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    // Act - start worker (database NOT ready)
    var workerTask = worker.StartAsync(cts.Token);

    // Wait for the worker to poll database readiness at least once while not ready
    await databaseReadiness.NotReadyCheckReceived;
    await Assert.That(publishStrategy.PublishedWork).Count().IsEqualTo(0)
      .Because("No messages should be published while database is not ready");
    await Assert.That(workCoordinator.ProcessWorkBatchCallCount).IsEqualTo(0)
      .Because("Coordinator should not be called while database is not ready");

    // Make database ready
    databaseReadiness.SetReady();

    // Wait for all messages to be published
    var allPublishedTask = publishStrategy.AllPublished;
    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
    await Task.WhenAny(allPublishedTask, timeoutTask);

    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected
    }

    // Assert - messages published after database became ready
    await Assert.That(publishStrategy.PublishedWork).Count().IsEqualTo(messageCount);
    await Assert.That(workCoordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1);
  }

  private static OutboxWork _createOutboxWork(Guid messageId, string destination) {
    return new OutboxWork {
      MessageId = messageId,
      Destination = destination,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Envelope = _createTestEnvelope(messageId),
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
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

  private sealed class TestWorkChannelWriter : IWorkChannelWriter {
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public void ClearInFlight() { }
    public bool ShouldRenewLease(Guid messageId) => false;
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
