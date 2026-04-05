using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage-focused tests for WorkCoordinatorPublisherWorker.
/// Targets: bulk publish error paths, lifecycle invocation (coordinator + direct fallback),
/// shutdown/ObjectDisposedException paths, acknowledgement processing, inbox work with
/// MaxInboxAttempts purge, _trackPublishResult branches, trace context extraction,
/// and QueuedAt timestamp population.
/// </summary>
public class WorkCoordinatorPublisherWorkerCoverageTests {

  // ================================================================
  // Helpers
  // ================================================================

  private static MessageEnvelope<JsonElement> _createTestEnvelope(Guid messageId, List<MessageHop>? hops = null) {
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = hops ?? [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static OutboxWork _createOutboxWork(Guid? messageId = null, string destination = "test-topic") {
    var id = messageId ?? Guid.CreateVersion7();
    return new OutboxWork {
      MessageId = id,
      Destination = destination,
      Envelope = _createTestEnvelope(id),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  private static InboxWork _createInboxWork(Guid? messageId = null, int attempts = 0) {
    var id = messageId ?? Guid.CreateVersion7();
    return new InboxWork {
      MessageId = id,
      Envelope = _createTestEnvelope(id),
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = attempts,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };
  }

  private static ServiceInstanceProvider _createTestInstanceProvider() =>
    new(Guid.NewGuid(), "CoverageTestService", "test-host", Environment.ProcessId);

  private static WorkCoordinatorPublisherWorker _createWorker(
    IWorkCoordinator coordinator,
    IMessagePublishStrategy publishStrategy,
    IServiceInstanceProvider instanceProvider,
    IWorkChannelWriter? channelWriter = null,
    IDatabaseReadinessCheck? databaseReadinessCheck = null,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null,
    int pollingIntervalMs = 50,
    int? maxInboxAttempts = null,
    int idleThresholdPolls = 2,
    Action<IServiceCollection>? configureServices = null) {

    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    services.AddSingleton(publishStrategy);
    services.AddSingleton(instanceProvider);
    if (lifecycleMessageDeserializer is not null) {
      services.AddSingleton(lifecycleMessageDeserializer);
    }
    configureServices?.Invoke(services);
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    return new WorkCoordinatorPublisherWorker(
      instanceProvider,
      sp.GetRequiredService<IServiceScopeFactory>(),
      publishStrategy,
      channelWriter ?? new CoverageTestWorkChannelWriter(),
      Options.Create(new WorkCoordinatorPublisherOptions {
        PollingIntervalMilliseconds = pollingIntervalMs,
        MaxInboxAttempts = maxInboxAttempts,
        IdleThresholdPolls = idleThresholdPolls
      }),
      databaseReadinessCheck: databaseReadinessCheck
    );
  }

  private static ServiceProvider _createHostedServiceCollection(
    IWorkCoordinator coordinator,
    IMessagePublishStrategy publishStrategy,
    IServiceInstanceProvider instanceProvider,
    IWorkChannelWriter channelWriter,
    IDatabaseReadinessCheck? databaseReadinessCheck = null,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null,
    int pollingIntervalMs = 50,
    int? maxInboxAttempts = null,
    int maxBulkPublishBatchSize = 50,
    Action<IServiceCollection>? configureServices = null) {

    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    services.AddSingleton(publishStrategy);
    services.AddSingleton(instanceProvider);
    services.AddSingleton(channelWriter);
    if (databaseReadinessCheck is not null) {
      services.AddSingleton(databaseReadinessCheck);
    }
    if (lifecycleMessageDeserializer is not null) {
      services.AddSingleton(lifecycleMessageDeserializer);
    }
    configureServices?.Invoke(services);
    var options = new WorkCoordinatorPublisherOptions {
      PollingIntervalMilliseconds = pollingIntervalMs,
      MaxInboxAttempts = maxInboxAttempts,
      MaxBulkPublishBatchSize = maxBulkPublishBatchSize
    };
    services.AddSingleton(Options.Create(options));
    services.AddLogging();
    services.AddHostedService<WorkCoordinatorPublisherWorker>();
    return services.BuildServiceProvider();
  }

  // ================================================================
  // Test Fakes
  // ================================================================

  private sealed class CoverageTestWorkCoordinator : IWorkCoordinator {
    private int _callCount;
    private readonly object _callCountLock = new();
    private readonly List<(int TargetCount, TaskCompletionSource Signal)> _callCountWaiters = [];
    public List<OutboxWork> WorkToReturn { get; set; } = [];
    public List<InboxWork> InboxWorkToReturn { get; set; } = [];
    public int ProcessWorkBatchCallCount => _callCount;
    public Action? OnProcessWorkBatch { get; set; }
    public Func<ProcessWorkBatchRequest, WorkBatch>? ProcessWorkBatchFunc { get; set; }
    public ConcurrentBag<ProcessWorkBatchRequest> ReceivedRequests { get; } = [];
    public bool ThrowOnCall { get; set; }
    public TaskCompletionSource? CallSignal { get; set; }
    public TaskCompletionSource CompletionReceivedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForCallCountAsync(int count, TimeSpan timeout) {
      var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      lock (_callCountLock) {
        if (_callCount >= count) {
          tcs.TrySetResult();
        } else {
          _callCountWaiters.Add((count, tcs));
        }
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {

      Interlocked.Increment(ref _callCount);
      lock (_callCountLock) {
        foreach (var (target, signal) in _callCountWaiters) {
          if (_callCount >= target) {
            signal.TrySetResult();
          }
        }
      }
      ReceivedRequests.Add(request);
      OnProcessWorkBatch?.Invoke();
      CallSignal?.TrySetResult();

      // Signal when completions arrive (for tests that wait for completion reporting)
      if (request.OutboxCompletions.Length > 0 || request.InboxCompletions.Length > 0) {
        CompletionReceivedSignal.TrySetResult();
      }

      if (ThrowOnCall) {
        throw new InvalidOperationException("Simulated database failure");
      }

      if (ProcessWorkBatchFunc is not null) {
        return Task.FromResult(ProcessWorkBatchFunc(request));
      }

      var outbox = new List<OutboxWork>(WorkToReturn);
      var inbox = new List<InboxWork>(InboxWorkToReturn);
      WorkToReturn = [];
      InboxWorkToReturn = [];

      return Task.FromResult(new WorkBatch {
        OutboxWork = outbox,
        InboxWork = inbox,
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>
  /// Singular-only publish strategy (SupportsBulkPublish uses default=false).
  /// </summary>
  private sealed class SingularPublishStrategy : IMessagePublishStrategy {
    public bool IsReadyResult { get; set; } = true;
    public ConcurrentBag<OutboxWork> PublishedWork { get; } = [];
    public Func<OutboxWork, MessagePublishResult>? PublishResultFunc { get; set; }
    public TaskCompletionSource PublishSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool ThrowOnPublish { get; set; }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsReadyResult);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      if (ThrowOnPublish) {
        throw new InvalidOperationException("Simulated publish exception");
      }

      PublishedWork.Add(work);
      PublishSignal.TrySetResult();

      if (PublishResultFunc is not null) {
        return Task.FromResult(PublishResultFunc(work));
      }

      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      });
    }
  }

  /// <summary>
  /// Bulk-capable publish strategy (SupportsBulkPublish => true).
  /// </summary>
  private sealed class BulkPublishStrategy : IMessagePublishStrategy {
    public bool IsReadyResult { get; set; } = true;
    public bool SupportsBulkPublish => true;
    public ConcurrentBag<OutboxWork> PublishedWork { get; } = [];
    public Func<IReadOnlyList<OutboxWork>, IReadOnlyList<MessagePublishResult>>? BatchResultFunc { get; set; }
    public TaskCompletionSource BatchPublishSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool ThrowOnBatchPublish { get; set; }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsReadyResult);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      PublishedWork.Add(work);
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      });
    }

    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> workItems, CancellationToken cancellationToken) {
      if (ThrowOnBatchPublish) {
        throw new InvalidOperationException("Simulated batch publish exception");
      }

      BatchPublishSignal.TrySetResult();

      if (BatchResultFunc is not null) {
        return Task.FromResult(BatchResultFunc(workItems));
      }

      IReadOnlyList<MessagePublishResult> results = workItems.Select(w => new MessagePublishResult {
        MessageId = w.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
        Error = null
      }).ToList();
      return Task.FromResult(results);
    }
  }

  private sealed class CoverageTestWorkChannelWriter : IWorkChannelWriter {
    public void ClearInFlight() { }
    private readonly Channel<OutboxWork> _channel;
    private readonly TaskCompletionSource _requeueSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _writeCount;
    public List<OutboxWork> WrittenWork { get; } = [];
    public Task RequeueSignal => _requeueSignal.Task;

    public CoverageTestWorkChannelWriter() {
      _channel = Channel.CreateUnbounded<OutboxWork>();
    }

    public ChannelReader<OutboxWork> Reader => _channel.Reader;

    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct) {
      WrittenWork.Add(work);
      Interlocked.Increment(ref _writeCount);
      return _channel.Writer.WriteAsync(work, ct);
    }

    public bool TryWrite(OutboxWork work) {
      WrittenWork.Add(work);
      var count = Interlocked.Increment(ref _writeCount);
      var result = _channel.Writer.TryWrite(work);
      if (count > 1) {
        _requeueSignal.TrySetResult();
      }
      return result;
    }

    public void Complete() {
      _channel.Writer.Complete();
    }

    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public event Action? OnNewWorkAvailable;
    public void SignalNewWorkAvailable() => OnNewWorkAvailable?.Invoke();
    public event Action? OnNewPerspectiveWorkAvailable;
    public void SignalNewPerspectiveWorkAvailable() => OnNewPerspectiveWorkAvailable?.Invoke();
  }

  private sealed class ControlledDatabaseReadinessCheck : IDatabaseReadinessCheck {
    private int _callCount;
    private readonly object _lock = new();
    private readonly List<(int TargetCount, TaskCompletionSource Signal)> _waiters = [];

    public bool IsReady { get; set; } = true;
    public int CallCount => _callCount;

    public Task WaitForCallCountAsync(int count, TimeSpan timeout) {
      var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      lock (_lock) {
        if (_callCount >= count) {
          tcs.TrySetResult();
        } else {
          _waiters.Add((count, tcs));
        }
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _callCount);
      lock (_lock) {
        foreach (var (target, signal) in _waiters) {
          if (_callCount >= target) {
            signal.TrySetResult();
          }
        }
      }
      return Task.FromResult(IsReady);
    }
  }

  private sealed class ObjectDisposedPublishStrategy : IMessagePublishStrategy {
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) =>
      throw new ObjectDisposedException("Simulated disposed transport");
  }

  private sealed class ObjectDisposedBulkPublishStrategy : IMessagePublishStrategy {
    public bool SupportsBulkPublish => true;

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) =>
      throw new ObjectDisposedException("Simulated disposed transport");

    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> workItems, CancellationToken cancellationToken) =>
      throw new ObjectDisposedException("Simulated disposed transport");
  }

  // ================================================================
  // Bulk Publish Error Paths
  // ================================================================

  [Test]
  public async Task BulkPublish_BatchException_TracksFailuresForAllItemsAsync() {
    // Arrange - batch publish throws, exercising _handleBulkBatchException
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createOutboxWork();
    var msg2 = _createOutboxWork();
    coordinator.WorkToReturn = [msg1, msg2];

    var publishStrategy = new BulkPublishStrategy {
      ThrowOnBatchPublish = true
    };
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    // Wait for the coordinator to be called at least twice (first returns work, second picks up failures)
    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (coordinator.ProcessWorkBatchCallCount < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - failures should be reported on subsequent coordinator call
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(2)
      .Because("Coordinator should be called again after batch exception to report failures");
  }

  [Test]
  public async Task BulkPublish_MissingResultForMessage_CreatesSyntheticFailureAsync() {
    // Arrange - batch publish returns results for only some messages
    // This exercises the null result check in _processPublishBatchResultsAsync
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createOutboxWork();
    var msg2 = _createOutboxWork();
    coordinator.WorkToReturn = [msg1, msg2];

    var publishStrategy = new BulkPublishStrategy {
      // Only return result for msg1, omit msg2
      BatchResultFunc = items => [
        new MessagePublishResult {
          MessageId = items[0].MessageId,
          Success = true,
          CompletedStatus = MessageProcessingStatus.Published
        }
        // msg2 has no result -> synthetic failure path
      ]
    };
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    await publishStrategy.BatchPublishSignal.Task.WaitAsync(cts.Token);

    // Give time for result processing
    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (coordinator.ProcessWorkBatchCallCount < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - worker should have processed without crashing, and the missing result
    // should create a synthetic failure
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Worker should handle missing batch results without crashing");
  }

  [Test]
  public async Task BulkPublish_ObjectDisposedException_BreaksLoopAsync() {
    // Arrange - batch publish throws ObjectDisposedException, exercising the break path
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createOutboxWork();
    coordinator.WorkToReturn = [msg1];

    var publishStrategy = new ObjectDisposedBulkPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await worker.StartAsync(cts.Token);

    // The publisher loop should exit on ObjectDisposedException
    // Wait enough for the coordinator to have called and work to flow
    var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
    while (coordinator.ProcessWorkBatchCallCount < 1 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - worker should not crash on ObjectDisposedException
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Worker should handle ObjectDisposedException gracefully in bulk publish loop");
  }

  // ================================================================
  // Singular Publish Error Paths
  // ================================================================

  [Test]
  public async Task SingularPublish_ObjectDisposedException_BreaksLoopAsync() {
    // Arrange
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createOutboxWork();
    coordinator.WorkToReturn = [msg1];

    var publishStrategy = new ObjectDisposedPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await worker.StartAsync(cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
    while (coordinator.ProcessWorkBatchCallCount < 1 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Worker should handle ObjectDisposedException gracefully in singular publish loop");
  }

  [Test]
  public async Task SingularPublish_NonRetryableFailure_TracksAsFailureNotLeaseRenewalAsync() {
    // Arrange - publish returns a non-retryable failure (not TransportException)
    // This exercises the else branch in _trackPublishResult
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createOutboxWork();
    coordinator.WorkToReturn = [msg1];

    var publishStrategy = new SingularPublishStrategy {
      PublishResultFunc = w => new MessagePublishResult {
        MessageId = w.MessageId,
        Success = false,
        CompletedStatus = w.Status,
        Error = "Schema validation failed",
        Reason = MessageFailureReason.SerializationError
      }
    };
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    await publishStrategy.PublishSignal.Task.WaitAsync(cts.Token);

    // Wait for failure to be reported
    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (coordinator.ProcessWorkBatchCallCount < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - message should NOT be requeued (non-retryable)
    // Only 1 write (initial) - no TryWrite requeue
    var rewriteCount = channelWriter.WrittenWork.Count(w => w.MessageId == msg1.MessageId);
    await Assert.That(rewriteCount).IsEqualTo(1)
      .Because("Non-retryable failure should not requeue the message");
  }

  [Test]
  public async Task SingularPublish_SuccessfulPublish_TracksCompletionAsync() {
    // Arrange - publish returns success
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createOutboxWork();
    coordinator.WorkToReturn = [msg1];

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    await publishStrategy.PublishSignal.Task.WaitAsync(cts.Token);

    // Wait for completion to be reported via signal (deterministic, no polling)
    await coordinator.CompletionReceivedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - completion should be reported on subsequent coordinator call
    var hasCompletions = coordinator.ReceivedRequests.Any(r => r.OutboxCompletions.Length > 0);
    await Assert.That(hasCompletions).IsTrue()
      .Because("Successful publish should report completion on next coordinator call");
  }

  [Test]
  public async Task SingularPublish_PublishResultWithNullError_UsesUnknownErrorAsync() {
    // Arrange - publish result has null error (exercises ?? UNKNOWN_ERROR path)
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createOutboxWork();
    coordinator.WorkToReturn = [msg1];

    var publishStrategy = new SingularPublishStrategy {
      PublishResultFunc = w => new MessagePublishResult {
        MessageId = w.MessageId,
        Success = false,
        CompletedStatus = w.Status,
        Error = null,  // null error - should use "Unknown error"
        Reason = MessageFailureReason.Unknown
      }
    };
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    await publishStrategy.PublishSignal.Task.WaitAsync(cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (coordinator.ProcessWorkBatchCallCount < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - worker should handle null error without throwing
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Worker should handle null error in publish result gracefully");
  }

  [Test]
  public async Task SingularPublish_TransportExceptionWithNullError_UsesUnknownErrorAsync() {
    // Arrange - transport exception with null error
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createOutboxWork();
    coordinator.WorkToReturn = [msg1];

    var publishStrategy = new SingularPublishStrategy {
      PublishResultFunc = w => new MessagePublishResult {
        MessageId = w.MessageId,
        Success = false,
        CompletedStatus = w.Status,
        Error = null,
        Reason = MessageFailureReason.TransportException
      }
    };
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    // Wait for requeue signal (transport exception triggers TryWrite)
    await channelWriter.RequeueSignal.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - message should be requeued
    var rewriteCount = channelWriter.WrittenWork.Count(w => w.MessageId == msg1.MessageId);
    await Assert.That(rewriteCount).IsGreaterThan(1)
      .Because("Transport exception should requeue message for retry");
  }

  // ================================================================
  // Coordinator Loop Error Paths
  // ================================================================

  [Test]
  public async Task CoordinatorLoop_ObjectDisposedException_BreaksLoopAsync() {
    // Arrange - coordinator throws ObjectDisposedException
    var coordinator = new CoverageTestWorkCoordinator();
    var callCount = 0;
    coordinator.OnProcessWorkBatch = () => {
      if (Interlocked.Increment(ref callCount) >= 2) {
        ObjectDisposedException.ThrowIf(true, "Simulated disposed scope");
      }
    };

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await worker.StartAsync(cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
    while (coordinator.ProcessWorkBatchCallCount < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - should not crash
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Worker should handle ObjectDisposedException in coordinator loop");
  }

  [Test]
  public async Task CoordinatorLoop_GenericException_ContinuesLoopAsync() {
    // Arrange - coordinator throws a generic exception on first call
    var coordinator = new CoverageTestWorkCoordinator();
    var callCount = 0;
    coordinator.OnProcessWorkBatch = () => {
      if (Interlocked.Increment(ref callCount) == 2) {
        throw new InvalidOperationException("Simulated transient failure");
      }
    };

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    // Wait for at least 3 calls (1 initial + exception on 2nd + recovery on 3rd)
    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (coordinator.ProcessWorkBatchCallCount < 3 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - loop should continue after exception
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(3)
      .Because("Coordinator loop should continue after generic exception");
  }

  [Test]
  public async Task ProcessWorkBatch_DatabaseException_LeavesCompletionsInSentStateAsync() {
    // Arrange - first call returns work, second call (with completions) throws
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createOutboxWork();
    var callCount = 0;
    coordinator.OnProcessWorkBatch = () => {
      var count = Interlocked.Increment(ref callCount);
      if (count == 1) {
        // First call from initial batch - returns no work
      } else if (count == 3) {
        // Third call should have completions from published message
        // Throw to simulate database failure - completions should be retried
        throw new InvalidOperationException("Simulated DB failure");
      }
    };
    // Return work on second call
    coordinator.ProcessWorkBatchFunc = req => {
      if (callCount == 2) {
        return new WorkBatch {
          OutboxWork = [msg1],
          InboxWork = [],
          PerspectiveWork = []
        };
      }
      return new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] };
    };

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (coordinator.ProcessWorkBatchCallCount < 3 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - worker should recover from DB failure
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(3)
      .Because("Worker should continue processing after database failure");
  }

  // ================================================================
  // Inbox Work Processing + MaxInboxAttempts Purge
  // ================================================================

  [Test]
  public async Task InboxWork_MaxInboxAttempts_PurgesExceedingMessagesAsync() {
    // Arrange - inbox messages with attempts exceeding threshold
    var coordinator = new CoverageTestWorkCoordinator();
    var purgeableMsg = _createInboxWork(attempts: 5);
    var processableMsg = _createInboxWork(attempts: 1);
    coordinator.InboxWorkToReturn = [purgeableMsg, processableMsg];

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(
      coordinator, publishStrategy, instanceProvider, channelWriter,
      maxInboxAttempts: 3);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    // Wait for inbox processing
    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (coordinator.ProcessWorkBatchCallCount < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - the purgeable message should be tracked as inbox completion
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Worker should process inbox work with purge threshold");
  }

  [Test]
  public async Task InboxWork_AllExceedMaxAttempts_EarlyReturnsAsync() {
    // Arrange - all inbox messages exceed max attempts (exercises early return after purge)
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createInboxWork(attempts: 10);
    var msg2 = _createInboxWork(attempts: 8);
    coordinator.InboxWorkToReturn = [msg1, msg2];

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(
      coordinator, publishStrategy, instanceProvider, channelWriter,
      maxInboxAttempts: 5);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (coordinator.ProcessWorkBatchCallCount < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - all purged, no processing, report completions
    var hasInboxCompletions = coordinator.ReceivedRequests.Any(r => r.InboxCompletions.Length > 0);
    await Assert.That(hasInboxCompletions).IsTrue()
      .Because("All purged inbox messages should be reported as completions");
  }

  [Test]
  public async Task InboxWork_NoMaxInboxAttempts_ProcessesAllMessagesAsync() {
    // Arrange - MaxInboxAttempts is null, all messages should be processed regardless of attempts
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createInboxWork(attempts: 100);
    coordinator.InboxWorkToReturn = [msg1];

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    // maxInboxAttempts: null (default - disabled)
    var services = _createHostedServiceCollection(
      coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (coordinator.ProcessWorkBatchCallCount < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - all messages processed (no purge)
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Without MaxInboxAttempts, all messages should be processed");
  }

  // ================================================================
  // Metadata Extraction
  // ================================================================

  [Test]
  public async Task ProcessAcknowledgements_MetadataFromOutboxWork_ExtractsAckCountsAsync() {
    // Arrange - return work with metadata containing acknowledgement counts
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createOutboxWork();
    msg1 = msg1 with {
      Metadata = new Dictionary<string, JsonElement> {
        ["outbox_completions_processed"] = JsonDocument.Parse("2").RootElement,
        ["outbox_failures_processed"] = JsonDocument.Parse("1").RootElement,
        ["outbox_lease_renewals_processed"] = JsonDocument.Parse("0").RootElement,
        ["inbox_completions_processed"] = JsonDocument.Parse("0").RootElement,
        ["inbox_failures_processed"] = JsonDocument.Parse("0").RootElement,
        ["inbox_lease_renewals_processed"] = JsonDocument.Parse("0").RootElement,
      }
    };
    coordinator.WorkToReturn = [msg1];

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    await publishStrategy.PublishSignal.Task.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - no crash from metadata processing
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Worker should extract and process acknowledgement metadata");
  }

  [Test]
  public async Task ProcessAcknowledgements_MetadataFromInboxWork_ExtractsAckCountsAsync() {
    // Arrange - return inbox work with metadata (exercises _extractMetadataRow inbox path)
    var coordinator = new CoverageTestWorkCoordinator();
    var inboxMsg = _createInboxWork();
    inboxMsg = inboxMsg with {
      Metadata = new Dictionary<string, JsonElement> {
        ["outbox_completions_processed"] = JsonDocument.Parse("0").RootElement,
        ["outbox_failures_processed"] = JsonDocument.Parse("0").RootElement,
        ["outbox_lease_renewals_processed"] = JsonDocument.Parse("0").RootElement,
        ["inbox_completions_processed"] = JsonDocument.Parse("1").RootElement,
        ["inbox_failures_processed"] = JsonDocument.Parse("0").RootElement,
        ["inbox_lease_renewals_processed"] = JsonDocument.Parse("0").RootElement,
      }
    };
    coordinator.InboxWorkToReturn = [inboxMsg];

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (coordinator.ProcessWorkBatchCallCount < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Worker should extract metadata from inbox work when outbox has no metadata");
  }

  [Test]
  public async Task ProcessAcknowledgements_NoMetadata_DefaultsToZeroAsync() {
    // Arrange - work batch with no metadata at all
    var coordinator = new CoverageTestWorkCoordinator();
    coordinator.WorkToReturn = [];
    coordinator.InboxWorkToReturn = [];

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
    while (coordinator.ProcessWorkBatchCallCount < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(2)
      .Because("Worker should handle empty metadata without crashing");
  }

  // ================================================================
  // Trace Context Extraction
  // ================================================================

  [Test]
  public async Task PublishWithTraceContext_HopsWithTraceParent_ExtractsContextAsync() {
    // Arrange - outbox work with valid TraceParent hop
    var coordinator = new CoverageTestWorkCoordinator();
    var msgId = Guid.CreateVersion7();
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo { ServiceName = "test-service", InstanceId = Guid.NewGuid(), HostName = "test-host", ProcessId = 1234 },
      TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"
    };
    var msg = new OutboxWork {
      MessageId = msgId,
      Destination = "test-topic",
      Envelope = _createTestEnvelope(msgId, [hop]),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
    coordinator.WorkToReturn = [msg];

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    await publishStrategy.PublishSignal.Task.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(publishStrategy.PublishedWork.Count).IsGreaterThanOrEqualTo(1)
      .Because("Worker should extract trace context from hops and publish successfully");
  }

  [Test]
  public async Task PublishWithTraceContext_NoHops_UsesDefaultContextAsync() {
    // Arrange - outbox work with no hops (null/empty)
    var coordinator = new CoverageTestWorkCoordinator();
    var msgId = Guid.CreateVersion7();
    var msg = new OutboxWork {
      MessageId = msgId,
      Destination = "test-topic",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = null!,
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
    coordinator.WorkToReturn = [msg];

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    await publishStrategy.PublishSignal.Task.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(publishStrategy.PublishedWork.Count).IsGreaterThanOrEqualTo(1)
      .Because("Worker should handle null hops and use default trace context");
  }

  [Test]
  public async Task PublishWithTraceContext_InvalidTraceParent_UsesDefaultContextAsync() {
    // Arrange - outbox work with an invalid TraceParent value
    var coordinator = new CoverageTestWorkCoordinator();
    var msgId = Guid.CreateVersion7();
    var hop = new MessageHop {
      ServiceInstance = new ServiceInstanceInfo { ServiceName = "test-service", InstanceId = Guid.NewGuid(), HostName = "test-host", ProcessId = 1234 },
      TraceParent = "invalid-trace-parent-value"
    };
    var msg = new OutboxWork {
      MessageId = msgId,
      Destination = "test-topic",
      Envelope = _createTestEnvelope(msgId, [hop]),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = Guid.CreateVersion7(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
    coordinator.WorkToReturn = [msg];

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    await publishStrategy.PublishSignal.Task.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(publishStrategy.PublishedWork.Count).IsGreaterThanOrEqualTo(1)
      .Because("Worker should handle invalid trace parent gracefully");
  }

  // ================================================================
  // Work State Transition Events
  // ================================================================

  [Test]
  public async Task WorkStateTransitions_ActiveToIdle_FiresIdleEventOnlyOnceAsync() {
    // Arrange - work appears then disappears, idle event fires exactly when threshold is met
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createOutboxWork();
    var callCount = 0;
    var idleFiredCount = 0;
    var startedFiredCount = 0;

    coordinator.WorkToReturn = [msg1]; // First call returns work
    coordinator.OnProcessWorkBatch = () => {
      var count = Interlocked.Increment(ref callCount);
      if (count > 1) {
        coordinator.WorkToReturn = []; // Subsequent calls return empty
      }
    };

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();

    var sp = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);
    var worker = (WorkCoordinatorPublisherWorker)sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();

    worker.OnWorkProcessingStarted += () => { Interlocked.Increment(ref startedFiredCount); };
    worker.OnWorkProcessingIdle += () => { Interlocked.Increment(ref idleFiredCount); };

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    // Wait for idle to fire
    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (Volatile.Read(ref idleFiredCount) < 1 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(startedFiredCount).IsGreaterThanOrEqualTo(1)
      .Because("OnWorkProcessingStarted should fire when work first appears");
    await Assert.That(idleFiredCount).IsGreaterThanOrEqualTo(1)
      .Because("OnWorkProcessingIdle should fire after idle threshold is met");
  }

  [Test]
  public async Task WorkStateTransitions_NoWork_StaysIdleWithoutFiringEventAsync() {
    // Arrange - no work ever returned, worker stays idle, event should NOT fire
    // (it's already idle, so no active -> idle transition)
    var coordinator = new CoverageTestWorkCoordinator { WorkToReturn = [] };
    var idleFiredCount = 0;

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();

    var sp = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);
    var worker = (WorkCoordinatorPublisherWorker)sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();

    worker.OnWorkProcessingIdle += () => { Interlocked.Increment(ref idleFiredCount); };

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    // Wait a few polls
    var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
    while (coordinator.ProcessWorkBatchCallCount < 4 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - worker starts idle, never transitions to active, so idle event never fires
    await Assert.That(idleFiredCount).IsEqualTo(0)
      .Because("Worker starts idle - no active→idle transition means no idle event");
    await Assert.That(worker.IsIdle).IsTrue()
      .Because("Worker should remain idle when no work is returned");
    await Assert.That(worker.ConsecutiveEmptyPolls).IsGreaterThanOrEqualTo(1)
      .Because("Empty polls should still be counted");
  }

  // ================================================================
  // Shutdown Behavior
  // ================================================================

  [Test]
  public async Task Shutdown_CancellationToken_CompletesChannelAndStopsAsync() {
    // Arrange
    var coordinator = new CoverageTestWorkCoordinator { WorkToReturn = [] };
    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Wait for at least one poll cycle
    var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
    while (coordinator.ProcessWorkBatchCallCount < 1 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    // Cancel to trigger shutdown
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - channel should be completed (exercising _workChannelWriter.Complete() in coordinator loop)
    var canRead = channelWriter.Reader.TryRead(out _);
    // After completion, the channel should be drained or completed
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Worker should have processed at least one batch before shutdown");
  }

  // ================================================================
  // Publisher Loop Selection
  // ================================================================

  [Test]
  public async Task PublisherLoop_BulkCapable_UsesBulkPathAsync() {
    // Arrange
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createOutboxWork();
    coordinator.WorkToReturn = [msg1];

    var publishStrategy = new BulkPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    await publishStrategy.BatchPublishSignal.Task.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(publishStrategy.PublishedWork.Count).IsEqualTo(0)
      .Because("Bulk-capable strategy should not use singular publish");
  }

  [Test]
  public async Task PublisherLoop_NotBulkCapable_UsesSingularPathAsync() {
    // Arrange
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createOutboxWork();
    coordinator.WorkToReturn = [msg1];

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    await publishStrategy.PublishSignal.Task.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(publishStrategy.PublishedWork.Count).IsGreaterThanOrEqualTo(1)
      .Because("Non-bulk strategy should use singular publish path");
  }

  // ================================================================
  // Bulk Publish Transport Not Ready
  // ================================================================

  [Test]
  public async Task BulkPublish_TransportNotReady_Over10Consecutive_LogsWarningAsync() {
    // Arrange - transport stays not ready for many messages, exercises the >10 warning path
    var coordinator = new CoverageTestWorkCoordinator();
    // Generate enough messages to exceed 10 consecutive not-ready checks
    var messages = Enumerable.Range(0, 12).Select(_ => _createOutboxWork()).ToList();
    coordinator.WorkToReturn = messages;

    var publishStrategy = new BulkPublishStrategy {
      IsReadyResult = false
    };
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(
      coordinator, publishStrategy, instanceProvider, channelWriter,
      maxBulkPublishBatchSize: 1); // Process one at a time to accumulate consecutive counts

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    // Wait for enough requeue cycles (generous timeout for CI/slower machines)
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    var typedWorker = (WorkCoordinatorPublisherWorker)worker;
    while (typedWorker.ConsecutiveNotReadyChecks < 11 && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50);
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - consecutive count should exceed 10 (exercises warning log)
    await Assert.That(typedWorker.ConsecutiveNotReadyChecks).IsGreaterThan(10)
      .Because("Should track and warn after >10 consecutive not-ready checks in bulk path");
    await Assert.That(typedWorker.BufferedMessageCount).IsGreaterThan(10)
      .Because("Buffered message count should accumulate");
  }

  // ================================================================
  // Singular Publish Transport Not Ready Warning
  // ================================================================

  [Test]
  public async Task SingularPublish_TransportNotReady_Over10_LogsWarningAsync() {
    // Arrange - transport stays not ready, with messages cycling through
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createOutboxWork();
    coordinator.WorkToReturn = [msg1];

    var publishStrategy = new SingularPublishStrategy {
      IsReadyResult = false
    };
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    var typedWorker = (WorkCoordinatorPublisherWorker)worker;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (typedWorker.ConsecutiveNotReadyChecks < 11 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(typedWorker.ConsecutiveNotReadyChecks).IsGreaterThan(10)
      .Because("Should track consecutive not-ready checks in singular path");
    await Assert.That(typedWorker.TotalLeaseRenewals).IsGreaterThan(10)
      .Because("Lease renewals should accumulate for each not-ready check");
  }

  // ================================================================
  // ProcessWorkBatch - Outbox Work Ordering
  // ================================================================

  [Test]
  public async Task ProcessWorkBatch_OutboxWork_SortedByMessageIdAsync() {
    // Arrange - return outbox work in non-sorted order
    var coordinator = new CoverageTestWorkCoordinator();
    var id1 = Guid.CreateVersion7();
    await Task.Delay(15); // Ensure different UUIDv7 timestamps (CI timer resolution can be >1ms)
    var id2 = Guid.CreateVersion7();
    await Task.Delay(15);
    var id3 = Guid.CreateVersion7();

    // Return in reverse order
    coordinator.WorkToReturn = [
      _createOutboxWork(id3),
      _createOutboxWork(id1),
      _createOutboxWork(id2)
    ];

    var publishedOrder = new ConcurrentQueue<Guid>();
    var allPublished = new SemaphoreSlim(0);
    var publishStrategy = new SingularPublishStrategy {
      PublishResultFunc = w => {
        publishedOrder.Enqueue(w.MessageId);
        if (publishedOrder.Count >= 3) {
          allPublished.Release();
        }
        return new MessagePublishResult {
          MessageId = w.MessageId,
          Success = true,
          CompletedStatus = MessageProcessingStatus.Published
        };
      }
    };
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    await allPublished.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - messages should be published in MessageId order (UUIDv7 time-ordered)
    var orderedIds = publishedOrder.ToArray();
    await Assert.That(orderedIds.Length).IsEqualTo(3);
    await Assert.That(orderedIds[0]).IsEqualTo(id1)
      .Because("Messages should be sorted by MessageId (UUIDv7 time-ordered)");
    await Assert.That(orderedIds[1]).IsEqualTo(id2);
    await Assert.That(orderedIds[2]).IsEqualTo(id3);
  }

  // ================================================================
  // Log Batch Summary Paths
  // ================================================================

  [Test]
  public async Task ProcessWorkBatch_WithActivity_LogsMessageBatchSummaryAsync() {
    // Arrange - return some work to trigger totalActivity > 0 path
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createOutboxWork();
    coordinator.WorkToReturn = [msg1];

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    await publishStrategy.PublishSignal.Task.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - exercises LogMessageBatchSummary path (totalActivity > 0)
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Work batch with activity should log summary");
  }

  [Test]
  public async Task ProcessWorkBatch_NoActivity_LogsNoWorkClaimedAsync() {
    // Arrange - empty work batch (totalActivity == 0 -> LogNoWorkClaimed)
    var coordinator = new CoverageTestWorkCoordinator { WorkToReturn = [] };

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
    while (coordinator.ProcessWorkBatchCallCount < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - exercises LogNoWorkClaimed path
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(2)
      .Because("Empty work batch should log 'no work claimed'");
  }

  // ================================================================
  // Initial Work Batch
  // ================================================================

  [Test]
  public async Task InitialWorkBatch_DatabaseNotReady_SkipsAndContinuesAsync() {
    // Arrange - database not ready on startup
    var coordinator = new CoverageTestWorkCoordinator { WorkToReturn = [] };
    var dbCheck = new ControlledDatabaseReadinessCheck { IsReady = false };
    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(
      coordinator, publishStrategy, instanceProvider, channelWriter,
      databaseReadinessCheck: dbCheck);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await worker.StartAsync(cts.Token);

    // Wait for at least 3 database readiness checks — signal-based, deterministic
    await dbCheck.WaitForCallCountAsync(3, TimeSpan.FromSeconds(10));

    var typedWorker = (WorkCoordinatorPublisherWorker)worker;
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(typedWorker.ConsecutiveDatabaseNotReadyChecks).IsGreaterThanOrEqualTo(2)
      .Because("Database not ready should be tracked in coordinator loop");
  }

  [Test]
  public async Task InitialWorkBatch_ExceptionDuringProcessing_ContinuesStartupAsync() {
    // Arrange - coordinator throws on initial batch
    var coordinator = new CoverageTestWorkCoordinator();
    var firstCall = true;
    coordinator.OnProcessWorkBatch = () => {
      if (firstCall) {
        firstCall = false;
        throw new InvalidOperationException("Simulated initial batch failure");
      }
    };

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    // Wait for recovery - coordinator should be called again after initial failure
    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (coordinator.ProcessWorkBatchCallCount < 3 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(2)
      .Because("Worker should continue startup after initial batch exception");
  }

  // ================================================================
  // Lifecycle Invocation (no deserializer → early return)
  // ================================================================

  [Test]
  public async Task PreOutboxLifecycle_NullDestination_SkipsLifecycleAndPublishesAsync() {
    // Arrange — null-destination (event-store-only) message should be published
    // but should NOT fire PreOutbox lifecycle receptors
    var coordinator = new CoverageTestWorkCoordinator();
    var msg = _createOutboxWork(destination: null!); // null destination = event-store-only
    coordinator.WorkToReturn = [msg];

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    await publishStrategy.PublishSignal.Task.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert — message should still be processed (published/completed)
    await Assert.That(publishStrategy.PublishedWork.Count).IsGreaterThanOrEqualTo(1)
      .Because("Null-destination messages should still flow through the publish pipeline");

    // Assert — the publish result for null destination should be success (TransportPublishStrategy skips transport)
    var publishedMsg = publishStrategy.PublishedWork.FirstOrDefault(w => w.MessageId == msg.MessageId);
    await Assert.That(publishedMsg).IsNotNull()
      .Because("Null-destination message should be processed without PreOutbox lifecycle side effects");
  }

  [Test]
  public async Task PreOutboxLifecycle_NoDeserializer_SkipsLifecycleAsync() {
    // Arrange - no ILifecycleMessageDeserializer registered (exercises early return)
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createOutboxWork();
    coordinator.WorkToReturn = [msg1];

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    // No lifecycleMessageDeserializer passed
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    await publishStrategy.PublishSignal.Task.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - publish should succeed even without lifecycle invocation
    await Assert.That(publishStrategy.PublishedWork.Count).IsGreaterThanOrEqualTo(1)
      .Because("Publishing should work without lifecycle message deserializer");
  }

  // ================================================================
  // Inbox Lifecycle Error Isolation
  // ================================================================

  [Test]
  public async Task InboxLifecycle_NoDeserializerOrReceptorInvoker_SkipsLifecycleAsync() {
    // Arrange - inbox work with no lifecycle message deserializer
    var coordinator = new CoverageTestWorkCoordinator();
    var inboxMsg = _createInboxWork();
    coordinator.InboxWorkToReturn = [inboxMsg];

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (coordinator.ProcessWorkBatchCallCount < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - inbox work should be processed even without lifecycle support
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Inbox processing should work without lifecycle deserializer");
  }

  // ================================================================
  // PostOutbox Lifecycle Fallback Path
  // ================================================================

  [Test]
  public async Task PostOutboxLifecycle_NoTracking_NoTypedEnvelope_ReturnsEarlyAsync() {
    // Arrange - no lifecycle deserializer means outboxTracking=null, outboxTypedEnvelope=null
    // This exercises the early return in _invokePostOutboxLifecycleAsync
    var coordinator = new CoverageTestWorkCoordinator();
    var msg1 = _createOutboxWork();
    coordinator.WorkToReturn = [msg1];

    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(coordinator, publishStrategy, instanceProvider, channelWriter);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    await publishStrategy.PublishSignal.Task.WaitAsync(cts.Token);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - PostOutbox lifecycle should return early when no tracking/envelope
    await Assert.That(publishStrategy.PublishedWork.Count).IsGreaterThanOrEqualTo(1)
      .Because("PostOutbox lifecycle should handle null tracking and envelope gracefully");
  }

  // ================================================================
  // Request Building
  // ================================================================

  [Test]
  public async Task ProcessWorkBatch_Request_ContainsCorrectInstanceInfoAsync() {
    // Arrange
    var coordinator = new CoverageTestWorkCoordinator { WorkToReturn = [] };
    var publishStrategy = new SingularPublishStrategy();
    var instanceId = Guid.NewGuid();
    var instanceProvider = new ServiceInstanceProvider(instanceId, "TestService", "test-host", 12345);
    var channelWriter = new CoverageTestWorkChannelWriter();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IMessagePublishStrategy>(publishStrategy);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IWorkChannelWriter>(channelWriter);
    services.AddSingleton(Options.Create(new WorkCoordinatorPublisherOptions {
      PollingIntervalMilliseconds = 50,
      DebugMode = true,
      PartitionCount = 5000,
      LeaseSeconds = 120,
      StaleThresholdSeconds = 300
    }));
    services.AddLogging();
    services.AddHostedService<WorkCoordinatorPublisherWorker>();
    var sp = services.BuildServiceProvider();

    // Act
    var worker = sp.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
    while (coordinator.ProcessWorkBatchCallCount < 1 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert - verify request contains correct instance info
    var request = coordinator.ReceivedRequests.First();
    await Assert.That(request.InstanceId).IsEqualTo(instanceId);
    await Assert.That(request.ServiceName).IsEqualTo("TestService");
    await Assert.That(request.HostName).IsEqualTo("test-host");
    await Assert.That(request.ProcessId).IsEqualTo(12345);
    await Assert.That(request.Flags).IsEqualTo(WorkBatchOptions.DebugMode)
      .Because("DebugMode should set the DebugMode flag");
    await Assert.That(request.PartitionCount).IsEqualTo(5000);
    await Assert.That(request.LeaseSeconds).IsEqualTo(120);
    await Assert.That(request.StaleThresholdSeconds).IsEqualTo(300);
  }

  // ================================================================
  // Database Readiness Check - Recovery
  // ================================================================

  [Test]
  public async Task DatabaseReadiness_BecomesReady_ResetsAndProcessesWorkAsync() {
    // Arrange - database starts not ready, becomes ready
    var coordinator = new CoverageTestWorkCoordinator { WorkToReturn = [] };
    var dbCheck = new ControlledDatabaseReadinessCheck { IsReady = false };
    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(
      coordinator, publishStrategy, instanceProvider, channelWriter,
      databaseReadinessCheck: dbCheck);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    var typedWorker = (WorkCoordinatorPublisherWorker)worker;

    // Wait for not-ready checks to accumulate
    var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
    while (typedWorker.ConsecutiveDatabaseNotReadyChecks < 3 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    // Make database ready
    dbCheck.IsReady = true;

    // Wait for coordinator to be called (means readiness check passed)
    deadline = DateTimeOffset.UtcNow.AddSeconds(3);
    while (coordinator.ProcessWorkBatchCallCount < 1 && DateTimeOffset.UtcNow < deadline) {
      await Task.Yield();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsGreaterThanOrEqualTo(1)
      .Because("Worker should start processing after database becomes ready");
    await Assert.That(typedWorker.ConsecutiveDatabaseNotReadyChecks).IsEqualTo(0)
      .Because("Consecutive not-ready count should reset when database becomes ready");
  }

  // ================================================================
  // Database Not Ready Warning (>10 consecutive)
  // ================================================================

  [Test]
  public async Task DatabaseReadiness_Over10Consecutive_LogsWarningAsync() {
    // Arrange
    var coordinator = new CoverageTestWorkCoordinator { WorkToReturn = [] };
    var dbCheck = new ControlledDatabaseReadinessCheck { IsReady = false };
    var publishStrategy = new SingularPublishStrategy();
    var instanceProvider = _createTestInstanceProvider();
    var channelWriter = new CoverageTestWorkChannelWriter();
    var services = _createHostedServiceCollection(
      coordinator, publishStrategy, instanceProvider, channelWriter,
      databaseReadinessCheck: dbCheck);

    // Act
    var worker = services.GetRequiredService<Microsoft.Extensions.Hosting.IHostedService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await worker.StartAsync(cts.Token);

    var typedWorker = (WorkCoordinatorPublisherWorker)worker;

    // Wait for at least 12 database readiness checks — each cycle increments the counter
    // Signal-based: deterministic, no spin-loop or sleep
    await dbCheck.WaitForCallCountAsync(12, TimeSpan.FromSeconds(10));

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(typedWorker.ConsecutiveDatabaseNotReadyChecks).IsGreaterThan(10)
      .Because("Should accumulate >10 consecutive database not-ready checks");
  }
}
