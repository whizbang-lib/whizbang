using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for <see cref="InboxHandlerWorker"/> — covers the disabled killswitch arms,
/// the ctor guards, and the flush-callback success/failure routing that runs when a
/// handler bundle is enqueued and drained by the inner <see cref="BatchFlusher{T}"/>.
/// </summary>
[Category("Workers")]
public sealed class InboxHandlerWorkerTests {
  private static InboxHandlerWorkerOptions _enabledOptions() => new() {
    Enabled = true,
    Flusher = new BatchFlusherOptions {
      MaxBatchSize = 10,
      CoalesceWindowMs = 5,
      ImmediateFlushThreshold = 1,
      ChannelCapacity = 100
    }
  };

  private static HandlerCommitRequest _request(Guid handlerId, Guid messageId) => new(
    HandlerId: handlerId,
    InstanceId: Guid.NewGuid(),
    ServiceName: "svc",
    HostName: "host",
    ProcessId: 1,
    PartitionCount: 2,
    InboxCompletion: new HandlerInboxCompletion(messageId, 0));

  // ==========================================================================
  // Constructor guard tests
  // ==========================================================================

  [Test]
  public async Task Constructor_ThrowsOnNullScopeFactoryAsync() {
    var options = Options.Create(_enabledOptions());
    await Assert.That(() => new InboxHandlerWorker(
      null!, new CapturingFailureChannel(), new SchemaReadyGate(), options,
      NullLogger<InboxHandlerWorker>.Instance))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_ThrowsOnNullFailureChannelAsync() {
    var options = Options.Create(_enabledOptions());
    await Assert.That(() => new InboxHandlerWorker(
      new StubScopeFactory(new StubCoordinator()), null!, new SchemaReadyGate(), options,
      NullLogger<InboxHandlerWorker>.Instance))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_ThrowsOnNullSchemaGateAsync() {
    var options = Options.Create(_enabledOptions());
    await Assert.That(() => new InboxHandlerWorker(
      new StubScopeFactory(new StubCoordinator()), new CapturingFailureChannel(), null!, options,
      NullLogger<InboxHandlerWorker>.Instance))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_ThrowsOnNullOptionsAsync() {
    await Assert.That(() => new InboxHandlerWorker(
      new StubScopeFactory(new StubCoordinator()), new CapturingFailureChannel(), new SchemaReadyGate(), null!,
      NullLogger<InboxHandlerWorker>.Instance))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_ThrowsOnNullLoggerAsync() {
    var options = Options.Create(_enabledOptions());
    await Assert.That(() => new InboxHandlerWorker(
      new StubScopeFactory(new StubCoordinator()), new CapturingFailureChannel(), new SchemaReadyGate(), options,
      null!))
      .Throws<ArgumentNullException>();
  }

  // ==========================================================================
  // Disabled killswitch tests
  // ==========================================================================

  [Test]
  public async Task ExecuteAsync_WhenDisabled_DoesNotCommitAndStopsCleanlyAsync() {
    // Arrange - Enabled=false: ExecuteAsync takes the disabled branch (LogDisabled + infinite delay)
    var opts = _enabledOptions();
    opts.Enabled = false;
    var coordinator = new StubCoordinator();
    var worker = new InboxHandlerWorker(
      new StubScopeFactory(coordinator), new CapturingFailureChannel(), _markedReadyGate(),
      Options.Create(opts), NullLogger<InboxHandlerWorker>.Instance);

    // Act - Start (invokes ExecuteAsync -> disabled arm), enqueue a request, then stop.
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.EnqueueAsync(_request(Guid.NewGuid(), Guid.NewGuid()), cts.Token);
    await worker.StopAsync(cts.Token);

    // Assert - the disabled flush-callback early-returns; coordinator never invoked.
    await Assert.That(coordinator.CallCount).IsEqualTo(0);
  }

  // ==========================================================================
  // Flush routing tests
  // ==========================================================================

  [Test]
  public async Task EnqueuedRequest_WhenCommitSucceeds_DoesNotEnqueueFailureAsync() {
    // Arrange
    var handlerId = Guid.NewGuid();
    var messageId = Guid.NewGuid();
    var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var coordinator = new StubCoordinator(reqs => {
      committed.TrySetResult();
      return [new HandlerBatchResult(reqs[0].HandlerId, Success: true, ErrorMessage: null)];
    });
    var failures = new CapturingFailureChannel();
    var worker = new InboxHandlerWorker(
      new StubScopeFactory(coordinator), failures, _markedReadyGate(),
      Options.Create(_enabledOptions()), NullLogger<InboxHandlerWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Act
    await worker.EnqueueAsync(_request(handlerId, messageId), cts.Token);
    await committed.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
    await worker.StopAsync(cts.Token);

    // Assert - success result routes nothing to the failure channel
    await Assert.That(coordinator.CallCount).IsGreaterThanOrEqualTo(1);
    await Assert.That(failures.Enqueued.Count).IsEqualTo(0);
  }

  [Test]
  public async Task EnqueuedRequest_WhenCommitFails_RoutesFailureToFailureChannelAsync() {
    // Arrange - coordinator returns a failing result for the handler
    var handlerId = Guid.NewGuid();
    var messageId = Guid.NewGuid();
    var coordinator = new StubCoordinator(reqs =>
      [new HandlerBatchResult(reqs[0].HandlerId, Success: false, ErrorMessage: "commit exploded")]);
    var failures = new CapturingFailureChannel();
    var worker = new InboxHandlerWorker(
      new StubScopeFactory(coordinator), failures, _markedReadyGate(),
      Options.Create(_enabledOptions()), NullLogger<InboxHandlerWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Act
    await worker.EnqueueAsync(_request(handlerId, messageId), cts.Token);
    var routed = await failures.WaitForOneAsync(TimeSpan.FromSeconds(5));
    await worker.StopAsync(cts.Token);

    // Assert - the failed handler's inbox message id is routed with the error text
    await Assert.That(routed.Category).IsEqualTo(WorkCategory.Inbox);
    await Assert.That(routed.Failure.MessageId).IsEqualTo(messageId);
    await Assert.That(routed.Failure.Error).IsEqualTo("commit exploded");
    await Assert.That(routed.Failure.Reason).IsEqualTo(MessageFailureReason.Unknown);
  }

  [Test]
  public async Task EnqueuedRequest_WhenCommitFailsWithNullError_RoutesUnknownAsync() {
    // Arrange - failing result with null ErrorMessage exercises the "?? unknown" fallback
    var handlerId = Guid.NewGuid();
    var messageId = Guid.NewGuid();
    var coordinator = new StubCoordinator(reqs =>
      [new HandlerBatchResult(reqs[0].HandlerId, Success: false, ErrorMessage: null)]);
    var failures = new CapturingFailureChannel();
    var worker = new InboxHandlerWorker(
      new StubScopeFactory(coordinator), failures, _markedReadyGate(),
      Options.Create(_enabledOptions()), NullLogger<InboxHandlerWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Act
    await worker.EnqueueAsync(_request(handlerId, messageId), cts.Token);
    var routed = await failures.WaitForOneAsync(TimeSpan.FromSeconds(5));
    await worker.StopAsync(cts.Token);

    // Assert
    await Assert.That(routed.Failure.Error).IsEqualTo("unknown");
  }

  [Test]
  public async Task EnqueueAsync_ThrowsOnNullRequestAsync() {
    var worker = new InboxHandlerWorker(
      new StubScopeFactory(new StubCoordinator()), new CapturingFailureChannel(), _markedReadyGate(),
      Options.Create(_enabledOptions()), NullLogger<InboxHandlerWorker>.Instance);

    await Assert.That(async () => await worker.EnqueueAsync(null!)).Throws<ArgumentNullException>();
  }

  // ==========================================================================
  // Test doubles
  // ==========================================================================

  private static SchemaReadyGate _markedReadyGate() {
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return gate;
  }

  private sealed class CapturingFailureChannel : IFailureChannel, IDisposable {
    public List<CategorizedFailure> Enqueued { get; } = [];
    private readonly SemaphoreSlim _signal = new(0);

    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      lock (Enqueued) {
        Enqueued.Add(new CategorizedFailure(category, failure));
      }
      _signal.Release();
      return ValueTask.CompletedTask;
    }

    public async Task<CategorizedFailure> WaitForOneAsync(TimeSpan timeout) {
      await _signal.WaitAsync(timeout);
      lock (Enqueued) {
        return Enqueued[^1];
      }
    }

    public void Dispose() => _signal.Dispose();
  }

  private sealed class StubCoordinator : MinimalWorkCoordinator {
    private readonly Func<IReadOnlyList<HandlerCommitRequest>, IReadOnlyList<HandlerBatchResult>>? _handler;
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public StubCoordinator() { }

    public StubCoordinator(Func<IReadOnlyList<HandlerCommitRequest>, IReadOnlyList<HandlerBatchResult>> handler) {
      _handler = handler;
    }

    public override Task<IReadOnlyList<HandlerBatchResult>> CommitHandlerBatchAsync(
      IReadOnlyList<HandlerCommitRequest> requests, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _callCount);
      var results = _handler?.Invoke(requests)
        ?? [.. requests.Select(r => new HandlerBatchResult(r.HandlerId, Success: true, ErrorMessage: null))];
      return Task.FromResult(results);
    }
  }

  // Minimal IWorkCoordinator implementation for the worker's single dependency.
  private abstract class MinimalWorkCoordinator : IWorkCoordinator {
    public virtual Task<IReadOnlyList<HandlerBatchResult>> CommitHandlerBatchAsync(
      IReadOnlyList<HandlerCommitRequest> requests, CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<HandlerBatchResult>>([]);

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken ct = default)
      => Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [], SyncInquiryResults = null });

    public Task<IReadOnlyList<SyncInquiryResult>> ResolveSyncInquiriesAsync(
      IReadOnlyList<SyncInquiry> inquiries, CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<SyncInquiryResult>>([]);

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default)
      => Task.CompletedTask;

    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default)
      => Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class StubScopeFactory(IWorkCoordinator coordinator) : IServiceScopeFactory {
    private readonly IWorkCoordinator _coordinator = coordinator;

    public IServiceScope CreateScope() => new StubScope(_coordinator);

    private sealed class StubScope(IWorkCoordinator coordinator) : IServiceScope {
      public IServiceProvider ServiceProvider { get; } = new StubProvider(coordinator);
      public void Dispose() { }
    }

    private sealed class StubProvider(IWorkCoordinator coordinator) : IServiceProvider {
      private readonly IWorkCoordinator _coordinator = coordinator;

      public object? GetService(Type serviceType)
        => serviceType == typeof(IWorkCoordinator) ? _coordinator : null;
    }
  }
}
