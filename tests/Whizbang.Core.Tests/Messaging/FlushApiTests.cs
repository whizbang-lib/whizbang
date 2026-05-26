using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for the split IWorkCoordinatorStrategy flush API:
///   FlushAsync(flags, ct) : Task            — fire-and-forget, strategy decides when to flush
///   FlushAndGetBatchAsync(flags, ct) : Task&lt;WorkBatch&gt; — force flush, bypass batching window
///
/// Replaces the old FlushMode-based API with two explicit methods so callers cannot accidentally
/// force synchronous flushes against an Interval or Batch strategy.
/// </summary>
/// <docs>data/work-coordinator-strategies</docs>
[Category("Core")]
[Category("Messaging")]
public class FlushApiTests {
  // ========================================
  // Scoped strategy
  // ========================================

  [Test]
  public async Task Scoped_FlushAsync_WithQueuedMessages_FlushesImmediatelyAsync() {
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createScopedStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    await strategy.FlushAsync(WorkBatchOptions.None);

    await TUnit.Assertions.Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Scoped flushes eagerly — DisposeAsync is unreliable, so even fire-and-forget flushes now");
  }

  [Test]
  public async Task Scoped_FlushAndGetBatchAsync_WithQueuedMessages_FlushesImmediatelyAsync() {
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createScopedStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    _ = await strategy.FlushAndGetBatchAsync(WorkBatchOptions.None);

    await TUnit.Assertions.Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task Scoped_FlushAndGetBatchAsync_EmptyQueues_ReturnsEmptyBatch_NoDbCallAsync() {
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createScopedStrategy(coordinator);

    var result = await strategy.FlushAndGetBatchAsync(WorkBatchOptions.None);

    await TUnit.Assertions.Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0);
    await TUnit.Assertions.Assert.That(result.OutboxWork).Count().IsEqualTo(0);
  }

  [Test]
  public async Task Scoped_FlushAsync_AfterDisposal_ThrowsObjectDisposedExceptionAsync() {
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createScopedStrategy(coordinator);
    await strategy.DisposeAsync();

    await TUnit.Assertions.Assert.That(async () => await strategy.FlushAsync(WorkBatchOptions.None))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task Scoped_FlushAndGetBatchAsync_AfterDisposal_ThrowsObjectDisposedExceptionAsync() {
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createScopedStrategy(coordinator);
    await strategy.DisposeAsync();

    await TUnit.Assertions.Assert.That(async () => await strategy.FlushAndGetBatchAsync(WorkBatchOptions.None))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task Scoped_FlushAndGetBatchAsync_DebugModeFlag_PropagatedAsync() {
    var coordinator = new FakeWorkCoordinatorWithFlags();
    var options = new WorkCoordinatorOptions { DebugMode = true };
    var strategy = _createScopedStrategy(coordinator, options);
    _queueTestOutboxMessage(strategy);

    _ = await strategy.FlushAndGetBatchAsync(WorkBatchOptions.None);

    await TUnit.Assertions.Assert.That(coordinator.LastFlags & WorkBatchOptions.DebugMode)
      .IsEqualTo(WorkBatchOptions.DebugMode);
  }

  [Test]
  public async Task Scoped_FlushAndGetBatchAsync_ClearsQueuesAfterFlushAsync() {
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createScopedStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    _ = await strategy.FlushAndGetBatchAsync(WorkBatchOptions.None);
    _ = await strategy.FlushAndGetBatchAsync(WorkBatchOptions.None);

    await TUnit.Assertions.Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("second flush with empty queues should skip DB call");
  }

  // ========================================
  // Immediate strategy
  // ========================================

  [Test]
  public async Task Immediate_FlushAsync_WithQueuedMessages_FlushesImmediatelyAsync() {
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createImmediateStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    await strategy.FlushAsync(WorkBatchOptions.None);

    await TUnit.Assertions.Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Immediate always flushes eagerly");
  }

  [Test]
  public async Task Immediate_FlushAndGetBatchAsync_WithQueuedMessages_ReturnsBatchAsync() {
    var messageId = Guid.CreateVersion7();
    var coordinator = new FakeWorkCoordinatorWithReturnedWork([
      new OutboxWork {
        MessageId = messageId,
        Destination = "test-topic",
        EnvelopeType = "TestEnvelope, TestAssembly",
        MessageType = "System.Text.Json.JsonElement, System.Text.Json",
        Envelope = _createTestEnvelope(messageId),
        Attempts = 0,
        Status = MessageProcessingStatus.None
      }
    ]);
    var strategy = _createImmediateStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    var result = await strategy.FlushAndGetBatchAsync(WorkBatchOptions.None);

    await TUnit.Assertions.Assert.That(result.OutboxWork).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Immediate_FlushAndGetBatchAsync_ClearsQueuesAfterFlushAsync() {
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createImmediateStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    _ = await strategy.FlushAndGetBatchAsync(WorkBatchOptions.None);
    await TUnit.Assertions.Assert.That(coordinator.LastNewOutboxMessages.Length).IsEqualTo(1);

    _ = await strategy.FlushAndGetBatchAsync(WorkBatchOptions.None);
    await TUnit.Assertions.Assert.That(coordinator.LastNewOutboxMessages.Length).IsEqualTo(0)
      .Because("second flush should have no outbox messages since queues were cleared");
  }

  // ========================================
  // Interval strategy
  // ========================================

  [Test]
  public async Task Interval_FlushAsync_WithQueuedMessages_DefersToTimer_NoDbCallAsync() {
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createIntervalStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    await strategy.FlushAsync(WorkBatchOptions.None);

    await TUnit.Assertions.Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0)
      .Because("Fire-and-forget FlushAsync on Interval defers to the timer — the cascade bug fix");

    await strategy.DisposeAsync();
  }

  [Test]
  public async Task Interval_FlushAsync_DoesNotClearQueues_ForwardedToLaterFlushAndGetBatchAsync() {
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createIntervalStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    await strategy.FlushAsync(WorkBatchOptions.None);
    _queueTestOutboxMessage(strategy);
    _ = await strategy.FlushAndGetBatchAsync(WorkBatchOptions.None);

    await TUnit.Assertions.Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    await TUnit.Assertions.Assert.That(coordinator.LastNewOutboxMessages.Length).IsEqualTo(2)
      .Because("both the deferred and the new message should be in the forced batch");

    await strategy.DisposeAsync();
  }

  [Test]
  public async Task Interval_FlushAndGetBatchAsync_WithQueuedMessages_FlushesImmediately_BypassesTimerAsync() {
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createIntervalStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    _ = await strategy.FlushAndGetBatchAsync(WorkBatchOptions.None);

    await TUnit.Assertions.Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("FlushAndGetBatchAsync bypasses the Interval timer");

    await strategy.DisposeAsync();
  }

  [Test]
  public async Task Interval_FlushAndGetBatchAsync_EmptyQueues_ReturnsEmptyBatchAsync() {
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createIntervalStrategy(coordinator);

    var result = await strategy.FlushAndGetBatchAsync(WorkBatchOptions.None);

    await TUnit.Assertions.Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0);
    await TUnit.Assertions.Assert.That(result.OutboxWork).Count().IsEqualTo(0);

    await strategy.DisposeAsync();
  }

  [Test]
  public async Task Interval_DisposeFlushesRemainingAsync() {
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createIntervalStrategy(coordinator);
    _queueTestOutboxMessage(strategy);
    await strategy.FlushAsync(WorkBatchOptions.None);

    await strategy.DisposeAsync();

    await TUnit.Assertions.Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("DisposeAsync should flush remaining queued messages");
  }

  [Test]
  public async Task Interval_FlushAsync_AfterDisposal_ThrowsObjectDisposedExceptionAsync() {
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createIntervalStrategy(coordinator);
    await strategy.DisposeAsync();

    await TUnit.Assertions.Assert.That(async () => await strategy.FlushAsync(WorkBatchOptions.None))
      .ThrowsExactly<ObjectDisposedException>();
  }

  // ========================================
  // Interval options
  // ========================================

  [Test]
  public async Task Interval_CoalesceWindowMilliseconds_Default0_NoCoalescingAsync() {
    var options = new WorkCoordinatorOptions { CoalesceWindowMilliseconds = 0 };
    await TUnit.Assertions.Assert.That(options.CoalesceWindowMilliseconds).IsEqualTo(0);
  }

  [Test]
  public async Task Interval_CoalesceWindowMilliseconds_ConfigurableViaOptionsAsync() {
    var options = new WorkCoordinatorOptions { CoalesceWindowMilliseconds = 50 };
    await TUnit.Assertions.Assert.That(options.CoalesceWindowMilliseconds).IsEqualTo(50);
  }

  // ========================================
  // Strategy configuration defaults
  // ========================================

  [Test]
  public async Task Strategy_Default_IsScopedAsync() {
    var options = new WorkCoordinatorOptions();
    await TUnit.Assertions.Assert.That(options.Strategy).IsEqualTo(WorkCoordinatorStrategy.Scoped);
  }

  [Test]
  public async Task Interval_DefaultIntervalMilliseconds_Is100Async() {
    var options = new WorkCoordinatorOptions();
    await TUnit.Assertions.Assert.That(options.IntervalMilliseconds).IsEqualTo(100);
  }

  // ========================================
  // Test helpers
  // ========================================

  private static TestMessageEnvelope _createTestEnvelope(Guid messageId) {
    return new TestMessageEnvelope {
      MessageId = MessageId.From(messageId),
      Hops = []
    };
  }

  private static void _queueTestOutboxMessage(IWorkCoordinatorStrategy strategy) {
    var messageId = Guid.CreateVersion7();
    strategy.QueueOutboxMessage(new OutboxMessage {
      MessageId = messageId,
      Destination = "test-topic",
      EnvelopeType = "TestEnvelope, TestAssembly",
      Envelope = _createTestEnvelope(messageId),
      IsEvent = false,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(messageId),
        Hops = []
      }
    });
  }

  private static ScopedWorkCoordinatorStrategy _createScopedStrategy(
    IWorkCoordinator coordinator,
    WorkCoordinatorOptions? options = null,
    IWorkChannelWriter? channelWriter = null) {
    return new ScopedWorkCoordinatorStrategy(
      coordinator,
      new FakeServiceInstanceProvider(),
      channelWriter,
      options ?? new WorkCoordinatorOptions()
    );
  }

  private static ImmediateWorkCoordinatorStrategy _createImmediateStrategy(IWorkCoordinator coordinator) {
    return new ImmediateWorkCoordinatorStrategy(
      coordinator,
      new FakeServiceInstanceProvider(),
      new WorkCoordinatorOptions()
    );
  }

  private static IntervalWorkCoordinatorStrategy _createIntervalStrategy(IWorkCoordinator coordinator) {
    return new IntervalWorkCoordinatorStrategy(
      coordinator,
      new FakeServiceInstanceProvider(),
      new WorkCoordinatorOptions { IntervalMilliseconds = 60_000 } // Long interval to prevent timer-based flushes
    );
  }

  // ========================================
  // Fakes
  // ========================================

  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    public int ProcessWorkBatchCallCount { get; private set; }
    public OutboxMessage[] LastNewOutboxMessages { get; private set; } = [];

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      // Legacy fallback (not in live path).
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task StoreOutboxMessagesAsync(
      OutboxMessage[] messages,
      int partitionCount = 2,
      CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      LastNewOutboxMessages = messages;
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) {
      ProcessWorkBatchCallCount++;
      return Task.CompletedTask;
    }

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class FakeWorkCoordinatorWithFlags : IWorkCoordinator {
    public WorkBatchOptions LastFlags { get; private set; }

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      LastFlags = request.Flags;
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class FakeWorkCoordinatorWithReturnedWork(List<OutboxWork> workToReturn) : IWorkCoordinator {
    private readonly List<OutboxWork> _workToReturn = workToReturn;

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      return Task.FromResult(new WorkBatch {
        OutboxWork = _workToReturn,
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName { get; } = "TestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 12345;

    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class TestMessageEnvelope : IMessageEnvelope<JsonElement> {
    public int Version => 1;
    public MessageDispatchContext DispatchContext { get; } = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local };
    public required MessageId MessageId { get; init; }
    public required List<MessageHop> Hops { get; init; }
    public JsonElement Payload { get; init; } = JsonDocument.Parse("{}").RootElement;
    object IMessageEnvelope.Payload => Payload;

    public void AddHop(MessageHop hop) => Hops.Add(hop);
    public DateTimeOffset GetMessageTimestamp() => Hops.Count > 0 ? Hops[0].Timestamp : DateTimeOffset.UtcNow;
    public CorrelationId? GetCorrelationId() => Hops.Count > 0 ? Hops[0].CorrelationId : null;
    public MessageId? GetCausationId() => Hops.Count > 0 ? Hops[0].CausationId : null;
    public JsonElement? GetMetadata(string key) => null;
    public SecurityContext? GetCurrentSecurityContext() => null;
    public ScopeContext? GetCurrentScope() => null;
  }
}
