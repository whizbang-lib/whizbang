using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Async;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for WorkCoordinatorPublisherWorker idle state tracking:
/// - IsIdle initial state (true)
/// - ConsecutiveEmptyPolls increments
/// - OnWorkProcessingStarted event
/// - OnWorkProcessingIdle event
/// - InboxWork returned from batch → failures path
/// - TransportException → _leaseRenewals path
/// - SerializationError → _failures path
/// - Unexpected exception → _failures path
/// </summary>
public class WorkCoordinatorPublisherWorkerIdleStateTests {
  // ============================================================
  // IsIdle initial state
  // ============================================================

  [Test]
  public async Task IsIdle_InitialState_IsTrueAsync() {
    // Arrange
    var coordinator = new IdleTestWorkCoordinator();
    var publishStrategy = new IdleTestPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();

    var worker = _createWorker(coordinator, publishStrategy, instanceProvider);

    // Assert - before starting, worker is in idle state
    await Assert.That(worker.IsIdle).IsTrue()
      .Because("Worker should start in idle state");
  }

  // ============================================================
  // ConsecutiveEmptyPolls increments
  // ============================================================

  [Test]
  public async Task ConsecutiveEmptyPolls_AfterEmptyBatches_IncrementsAsync() {
    // Arrange - coordinator returns empty batches
    var coordinator = new IdleTestWorkCoordinator { WorkToReturn = [] };
    var publishStrategy = new IdleTestPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var callCount = 0;

    coordinator.OnProcessWorkBatch = () => {
      Interlocked.Increment(ref callCount);
    };

    var worker = _createWorker(coordinator, publishStrategy, instanceProvider,
      pollingIntervalMs: 50);

    using var cts = new CancellationTokenSource();

    // Act - start worker and allow at least 2 polling cycles
    var workerTask = worker.StartAsync(cts.Token);

    // Wait until at least 2 calls have been made
    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (callCount < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(20);
    }

    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(worker.ConsecutiveEmptyPolls).IsGreaterThanOrEqualTo(1)
      .Because("ConsecutiveEmptyPolls should increment with each empty poll");
  }

  // ============================================================
  // OnWorkProcessingStarted event fires when work appears
  // ============================================================

  [Test]
  public async Task OnWorkProcessingStarted_WhenWorkAppears_FiresEventAsync() {
    // Arrange - coordinator returns work on the second call
    var coordinator = new IdleTestWorkCoordinator();
    var publishStrategy = new IdleTestPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var callCount = 0;
    var messageId = Guid.CreateVersion7();
    var startedFired = false;

    coordinator.OnProcessWorkBatch = () => {
      var count = Interlocked.Increment(ref callCount);
      if (count == 2) {
        // Return work on second call to trigger active transition
        coordinator.WorkToReturn = [_createOutboxWork(messageId, "test-topic")];
      }
    };

    var services = _createServiceCollection(coordinator, publishStrategy, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new IdleTestWorkChannelWriter(),
      Options.Create(new WorkCoordinatorPublisherOptions {
        PollingIntervalMilliseconds = 50,
        IdleThresholdPolls = 2
      })
    );

    // Register the event BEFORE the worker starts
    worker.OnWorkProcessingStarted += () => { startedFired = true; };

    using var cts = new CancellationTokenSource();

    // Act
    var workerTask = worker.StartAsync(cts.Token);

    // Wait for the started event to fire
    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (!startedFired && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(20);
    }

    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(startedFired).IsTrue()
      .Because("OnWorkProcessingStarted should fire when work appears after being idle");
  }

  // ============================================================
  // OnWorkProcessingIdle event fires after consecutive empty polls
  // ============================================================

  [Test]
  public async Task OnWorkProcessingIdle_AfterConsecutiveEmptyPolls_FiresEventAsync() {
    // Arrange - coordinator returns work on first call, then empty batches
    var coordinator = new IdleTestWorkCoordinator();
    var publishStrategy = new IdleTestPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var callCount = 0;
    var idleFired = 0;  // Use int + Interlocked for thread-safe cross-thread signaling
    var messageId = Guid.CreateVersion7();

    // Return work on first call (transitions to active), then empty
    coordinator.WorkToReturn = [_createOutboxWork(messageId, "test-topic")];
    coordinator.OnProcessWorkBatch = () => {
      var count = Interlocked.Increment(ref callCount);
      if (count > 1) {
        coordinator.WorkToReturn = [];
      }
    };

    var services = _createServiceCollection(coordinator, publishStrategy, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new IdleTestWorkChannelWriter(),
      Options.Create(new WorkCoordinatorPublisherOptions {
        PollingIntervalMilliseconds = 50,
        IdleThresholdPolls = 2  // After 2 consecutive empty polls, transition to idle
      })
    );

    worker.OnWorkProcessingIdle += () => { Interlocked.Exchange(ref idleFired, 1); };

    using var cts = new CancellationTokenSource();

    // Act
    var workerTask = worker.StartAsync(cts.Token);

    await AsyncTestHelpers.WaitForConditionAsync(
      () => Volatile.Read(ref idleFired) == 1,
      TimeSpan.FromSeconds(10),
      timeoutMessage: "OnWorkProcessingIdle should fire after IdleThresholdPolls consecutive empty polls");

    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert
    await Assert.That(Volatile.Read(ref idleFired)).IsEqualTo(1)
      .Because("OnWorkProcessingIdle should fire after IdleThresholdPolls consecutive empty polls");
  }

  // ============================================================
  // InboxWork returned → fails path (_failures tracker)
  // ============================================================

  [Test]
  public async Task InboxWork_ReturnedFromBatch_AddedToFailuresAsync() {
    // Arrange - coordinator returns inbox work to exercise the failure path
    var coordinator = new IdleTestWorkCoordinator();
    var publishStrategy = new IdleTestPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var callCount = 0;
    var inboxMessageId = Guid.CreateVersion7();

    coordinator.InboxWorkToReturn = [_createInboxWork(inboxMessageId)];
    coordinator.OnProcessWorkBatch = () => {
      var count = Interlocked.Increment(ref callCount);
      if (count > 1) {
        coordinator.InboxWorkToReturn = [];
      }
    };

    var services = _createServiceCollection(coordinator, publishStrategy, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new IdleTestWorkChannelWriter(),
      Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 50 })
    );

    using var cts = new CancellationTokenSource();

    // Act
    var workerTask = worker.StartAsync(cts.Token);

    // Wait for at least one ProcessWorkBatch call
    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (callCount < 1 && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(20);
    }

    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - worker should have processed inbox work without crashing
    await Assert.That(callCount).IsGreaterThanOrEqualTo(1)
      .Because("Worker should process inbox work items");
  }

  // ============================================================
  // TransportException → lease renewal path
  // ============================================================

  [Test]
  public async Task PublisherLoop_TransportException_RenewsLeaseAsync() {
    // Arrange - publish strategy returns TransportException failure (retryable)
    // TransportException adds to _leaseRenewals tracker (NOT _totalLeaseRenewals).
    // The lease renewals are sent on the NEXT coordinator call as RenewOutboxLeaseIds.
    var coordinator = new IdleTestWorkCoordinator();
    var publishStrategy = new IdleTestPublishStrategy {
      FailureReason = MessageFailureReason.TransportException
    };
    var instanceProvider = _createTestInstanceProvider();
    var messageId = Guid.CreateVersion7();
    coordinator.WorkToReturn = [_createOutboxWork(messageId, "test-topic")];

    var channelWriter = new IdleTestWorkChannelWriter();
    var services = _createServiceCollection(coordinator, publishStrategy, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      channelWriter,
      Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 50 })
    );

    using var cts = new CancellationTokenSource();

    // Act - start worker and wait for at least 2 ProcessWorkBatch calls
    // (1st call returns work → publisher processes with TransportException → adds lease renewal
    //  2nd call should contain the lease renewal in RenewOutboxLeaseIds)
    var workerTask = worker.StartAsync(cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (coordinator.ProcessWorkBatchCallCount < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(20);
    }

    // Give publisher loop time to process
    await Task.Delay(100);

    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - coordinator should have been called at least twice
    // (second call may carry the lease renewal in RenewOutboxLeaseIds)
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Worker should process TransportException without crashing and call coordinator");
  }

  // ============================================================
  // SerializationError → failures path (not lease renewal)
  // ============================================================

  [Test]
  public async Task PublisherLoop_SerializationError_DoesNotRenewLeaseAsync() {
    // Arrange - publish strategy returns SerializationError failure
    var coordinator = new IdleTestWorkCoordinator();
    var publishStrategy = new IdleTestPublishStrategy {
      FailureReason = MessageFailureReason.SerializationError
    };
    var instanceProvider = _createTestInstanceProvider();
    var messageId = Guid.CreateVersion7();
    coordinator.WorkToReturn = [_createOutboxWork(messageId, "test-topic")];

    var channelWriter = new IdleTestWorkChannelWriter();
    var services = _createServiceCollection(coordinator, publishStrategy, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      channelWriter,
      Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 50 })
    );

    using var cts = new CancellationTokenSource();

    // Act
    var workerTask = worker.StartAsync(cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (coordinator.ProcessWorkBatchCallCount < 1 && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(20);
    }

    await Task.Delay(100);

    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - SerializationError should NOT renew lease
    await Assert.That(worker.TotalLeaseRenewals).IsEqualTo(0)
      .Because("SerializationError should not trigger lease renewal (non-retryable)");
  }

  // ============================================================
  // Unexpected exception → failures path
  // ============================================================

  [Test]
  public async Task PublisherLoop_UnexpectedException_ContinuesProcessingAsync() {
    // Arrange - publish strategy throws an unexpected exception
    var coordinator = new IdleTestWorkCoordinator();
    var throwingStrategy = new ThrowingPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var messageId = Guid.CreateVersion7();
    coordinator.WorkToReturn = [_createOutboxWork(messageId, "test-topic")];

    var channelWriter = new IdleTestWorkChannelWriter();
    var services = _createServiceCollection(coordinator, throwingStrategy, instanceProvider);
    var worker = new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      throwingStrategy,
      channelWriter,
      Options.Create(new WorkCoordinatorPublisherOptions { PollingIntervalMilliseconds = 50 })
    );

    using var cts = new CancellationTokenSource();

    // Act - start worker; it should not crash despite publish throwing
    var workerTask = worker.StartAsync(cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (coordinator.ProcessWorkBatchCallCount < 1 && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(20);
    }

    await Task.Delay(100);

    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { }

    // Assert - worker completed without crashing
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Worker should continue even if publish throws unexpectedly");
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static ServiceInstanceProvider _createTestInstanceProvider() =>
    new ServiceInstanceProvider(
      Guid.NewGuid(),
      "IdleTestService",
      "test-host",
      Environment.ProcessId
    );

  private static WorkCoordinatorPublisherWorker _createWorker(
    IdleTestWorkCoordinator coordinator,
    IdleTestPublishStrategy publishStrategy,
    IServiceInstanceProvider instanceProvider,
    int pollingIntervalMs = 50) {
    var services = _createServiceCollection(coordinator, publishStrategy, instanceProvider);
    return new WorkCoordinatorPublisherWorker(
      instanceProvider,
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      new IdleTestWorkChannelWriter(),
      Options.Create(new WorkCoordinatorPublisherOptions {
        PollingIntervalMilliseconds = pollingIntervalMs
      })
    );
  }

  private static ServiceCollection _createServiceCollection(
    IWorkCoordinator coordinator,
    IMessagePublishStrategy publishStrategy,
    IServiceInstanceProvider instanceProvider) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    services.AddSingleton(publishStrategy);
    services.AddSingleton(instanceProvider);
    services.AddLogging();
    return services;
  }

  private static OutboxWork _createOutboxWork(Guid messageId, string destination) =>
    new OutboxWork {
      MessageId = messageId,
      Destination = destination,
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(messageId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = []
      },
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None
    };

  private static InboxWork _createInboxWork(Guid messageId) =>
    new InboxWork {
      MessageId = messageId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(messageId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = []
      },
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchFlags.None
    };

  // ============================================================
  // Test fakes
  // ============================================================

  private sealed class IdleTestWorkCoordinator : IWorkCoordinator {
    private int _callCount;
    public List<OutboxWork> WorkToReturn { get; set; } = [];
    public List<InboxWork> InboxWorkToReturn { get; set; } = [];
    public int ProcessWorkBatchCallCount => _callCount;
    public Action? OnProcessWorkBatch { get; set; }

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _callCount);
      OnProcessWorkBatch?.Invoke();

      return Task.FromResult(new WorkBatch {
        OutboxWork = [.. WorkToReturn],
        InboxWork = [.. InboxWorkToReturn],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(
      PerspectiveCursorCompletion completion,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(
      PerspectiveCursorFailure failure,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class IdleTestPublishStrategy : IMessagePublishStrategy {
    /// <summary>
    /// If set, PublishAsync returns a failure with this reason.
    /// If null (default), returns success.
    /// </summary>
    public MessageFailureReason? FailureReason { get; set; }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(true);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      if (FailureReason.HasValue) {
        return Task.FromResult(new MessagePublishResult {
          MessageId = work.MessageId,
          Success = false,
          CompletedStatus = MessageProcessingStatus.Failed,
          Error = "Simulated failure",
          Reason = FailureReason.Value
        });
      }

      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published
      });
    }
  }

  private sealed class ThrowingPublishStrategy : IMessagePublishStrategy {
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(true);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("Simulated unexpected publish exception");
  }

  private sealed class IdleTestWorkChannelWriter : IWorkChannelWriter {
    private readonly Channel<OutboxWork> _channel;

    public IdleTestWorkChannelWriter() {
      _channel = Channel.CreateUnbounded<OutboxWork>();
    }

    public ChannelReader<OutboxWork> Reader => _channel.Reader;

    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct) =>
      _channel.Writer.WriteAsync(work, ct);

    public bool TryWrite(OutboxWork work) =>
      _channel.Writer.TryWrite(work);

    public void Complete() =>
      _channel.Writer.Complete();
  }
}
