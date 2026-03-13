using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for FlushMode enum and its effect on each strategy implementation.
/// </summary>
/// <docs>data/work-coordinator-strategies</docs>
[Category("Core")]
[Category("Messaging")]
public class FlushModeTests {
  // ========================================
  // FlushMode Enum
  // ========================================

  [Test]
  public async Task FlushMode_Required_HasValue0Async() {
    int value = (int)FlushMode.Required;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task FlushMode_BestEffort_HasValue1Async() {
    int value = (int)FlushMode.BestEffort;
    await Assert.That(value).IsEqualTo(1);
  }

  // ========================================
  // Scoped + FlushMode
  // ========================================

  [Test]
  public async Task Scoped_FlushMode_Required_FlushesImmediately_CallsProcessWorkBatchAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createScopedStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    // Act
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.Required);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Required flush should call ProcessWorkBatchAsync immediately");
  }

  [Test]
  public async Task Scoped_FlushMode_Required_EmptyQueues_ReturnsEmptyBatch_NoDbCallAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createScopedStrategy(coordinator);

    // Act - no messages queued
    var result = await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.Required);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0)
      .Because("empty queues should skip ProcessWorkBatchAsync");
    await Assert.That(result.OutboxWork).Count().IsEqualTo(0);
  }

  [Test]
  public async Task Scoped_FlushMode_BestEffort_FlushesImmediately_SameAsRequiredAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createScopedStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    // Act
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.BestEffort);

    // Assert - Scoped strategy always flushes immediately because deferring to
    // DisposeAsync is unreliable (DbContext may already be disposed by DI container)
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Scoped strategy flushes immediately even in BestEffort mode to avoid DbContext disposal issues");
  }

  [Test]
  public async Task Scoped_FlushMode_BestEffort_DisposeHasNothingToFlushAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createScopedStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    // Act - BestEffort flushes immediately on Scoped
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.BestEffort);

    // Assert - already flushed, so dispose is a no-op
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("BestEffort already flushed on Scoped strategy");

    // Disposal should have nothing left to flush
    await strategy.DisposeAsync();
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("DisposeAsync should not flush again — already flushed");
  }

  [Test]
  public async Task Scoped_FlushMode_Required_AfterBestEffort_EachFlushesIndependentlyAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createScopedStrategy(coordinator);

    // Queue first message and BestEffort (flushes immediately on Scoped)
    _queueTestOutboxMessage(strategy);
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.BestEffort);

    // Queue second message and Required
    _queueTestOutboxMessage(strategy);
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.Required);

    // Assert - two separate flushes, one message each
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(2)
      .Because("each flush should execute independently on Scoped strategy");
    await Assert.That(coordinator.LastNewOutboxMessages.Length).IsEqualTo(1)
      .Because("the second flush should only contain the second message");
  }

  [Test]
  public async Task Scoped_FlushMode_Default_IsRequired_BackwardsCompatibleAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createScopedStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    // Act - call without specifying mode (should default to Required)
    await strategy.FlushAsync(WorkBatchFlags.None);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("default FlushMode should be Required for backwards compatibility");
  }

  [Test]
  public async Task Scoped_FlushMode_BestEffort_FlushesAllQueuedMessagesAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createScopedStrategy(coordinator);
    _queueTestOutboxMessage(strategy);
    _queueTestOutboxMessage(strategy);

    // Act - BestEffort flushes immediately on Scoped (both messages in one batch)
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.BestEffort);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("BestEffort on Scoped should flush immediately");
    await Assert.That(coordinator.LastNewOutboxMessages.Length).IsEqualTo(2)
      .Because("both queued messages should be in the batch");
  }

  [Test]
  public async Task Scoped_FlushMode_Required_ClearsQueuesAfterFlushAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createScopedStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    // Act
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.Required);

    // Assert - queues should be cleared, second flush should be empty
    var result = await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.Required);
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("second flush with empty queues should skip DB call");
  }

  [Test]
  public async Task Scoped_FlushMode_Required_DisposedState_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createScopedStrategy(coordinator);
    await strategy.DisposeAsync();

    // Act & Assert
    await Assert.That(async () => await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.Required))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task Scoped_FlushMode_BestEffort_DisposedState_ThrowsObjectDisposedExceptionAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createScopedStrategy(coordinator);
    await strategy.DisposeAsync();

    // Act & Assert
    await Assert.That(async () => await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.BestEffort))
      .ThrowsExactly<ObjectDisposedException>();
  }

  [Test]
  public async Task Scoped_FlushMode_Required_DebugModeFlag_PropagatedAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinatorWithFlags();
    var options = new WorkCoordinatorOptions { DebugMode = true };
    var strategy = _createScopedStrategy(coordinator, options);
    _queueTestOutboxMessage(strategy);

    // Act
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.Required);

    // Assert
    await Assert.That(coordinator.LastFlags & WorkBatchFlags.DebugMode).IsEqualTo(WorkBatchFlags.DebugMode)
      .Because("DebugMode flag should be propagated on Required flush");
  }

  [Test]
  public async Task Scoped_FlushMode_Required_WritesReturnedWorkToChannelAsync() {
    // Arrange
    var channelWriter = new FakeWorkChannelWriter();
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
    var strategy = _createScopedStrategy(coordinator, channelWriter: channelWriter);
    _queueTestOutboxMessage(strategy);

    // Act
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.Required);

    // Assert
    await Assert.That(channelWriter.WrittenWork).Count().IsEqualTo(1)
      .Because("returned work should be written to channel immediately");
    await Assert.That(channelWriter.WrittenWork[0].MessageId).IsEqualTo(messageId);
  }

  // ========================================
  // Immediate + FlushMode
  // ========================================

  [Test]
  public async Task Immediate_FlushMode_Required_FlushesImmediatelyAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createImmediateStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    // Act
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.Required);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task Immediate_FlushMode_BestEffort_AlsoFlushesImmediately_IgnoresModeAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createImmediateStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    // Act
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.BestEffort);

    // Assert - Immediate strategy ignores FlushMode
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Immediate strategy always flushes regardless of FlushMode");
  }

  [Test]
  public async Task Immediate_FlushMode_BestEffort_ReturnsActualWorkBatch_NotEmptyAsync() {
    // Arrange
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

    // Act
    var result = await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.BestEffort);

    // Assert - Immediate returns actual work, not empty
    await Assert.That(result.OutboxWork).Count().IsEqualTo(1)
      .Because("Immediate strategy should return actual work even in BestEffort mode");
  }

  [Test]
  public async Task Immediate_FlushMode_Default_IsRequired_BackwardsCompatibleAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createImmediateStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    // Act - no mode specified
    await strategy.FlushAsync(WorkBatchFlags.None);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task Immediate_FlushMode_BestEffort_ClearsQueuesAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createImmediateStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    // Act - BestEffort on Immediate still flushes
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.BestEffort);

    // Assert - first flush should have 1 outbox message
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Immediate always flushes, even in BestEffort mode");
    await Assert.That(coordinator.LastNewOutboxMessages.Length).IsEqualTo(1)
      .Because("the queued message should have been sent");

    // Second call with empty queues - still calls ProcessWorkBatch (Immediate doesn't skip)
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.Required);
    await Assert.That(coordinator.LastNewOutboxMessages.Length).IsEqualTo(0)
      .Because("second flush should have no outbox messages since queues were cleared");
  }

  // ========================================
  // Interval + FlushMode
  // ========================================

  [Test]
  public async Task Interval_FlushMode_Required_FlushesImmediately_BypassesTimerAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createIntervalStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    // Act
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.Required);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("Required flush should bypass timer and flush immediately");

    await strategy.DisposeAsync();
  }

  [Test]
  public async Task Interval_FlushMode_Required_EmptyQueues_ReturnsEmptyBatchAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createIntervalStrategy(coordinator);

    // Act
    var result = await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.Required);

    // Assert
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0);
    await Assert.That(result.OutboxWork).Count().IsEqualTo(0);

    await strategy.DisposeAsync();
  }

  [Test]
  public async Task Interval_FlushMode_BestEffort_ReturnsEmptyWorkBatchAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createIntervalStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    // Act
    var result = await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.BestEffort);

    // Assert - BestEffort returns empty immediately
    await Assert.That(result.OutboxWork).Count().IsEqualTo(0)
      .Because("BestEffort should return empty batch, deferring to timer");
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0);

    await strategy.DisposeAsync();
  }

  [Test]
  public async Task Interval_FlushMode_BestEffort_DefersUntilTimerAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createIntervalStrategy(coordinator);
    _queueTestOutboxMessage(strategy);

    // Act
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.BestEffort);

    // Assert - Not flushed yet (deferred to timer)
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(0)
      .Because("BestEffort should defer flush to timer cycle");

    // Cleanup - dispose will flush remaining
    await strategy.DisposeAsync();
  }

  [Test]
  public async Task Interval_FlushMode_Required_IncludesPreviouslyDeferredBestEffortItemsAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createIntervalStrategy(coordinator);

    // Queue and defer via BestEffort
    _queueTestOutboxMessage(strategy);
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.BestEffort);

    // Queue another and Required flush
    _queueTestOutboxMessage(strategy);
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.Required);

    // Assert - both messages included
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1);
    await Assert.That(coordinator.LastNewOutboxMessages.Length).IsEqualTo(2)
      .Because("Required flush should include previously deferred BestEffort items");

    await strategy.DisposeAsync();
  }

  [Test]
  public async Task Interval_FlushMode_BestEffort_DisposeFlushesRemainingAsync() {
    // Arrange
    var coordinator = new FakeWorkCoordinator();
    var strategy = _createIntervalStrategy(coordinator);
    _queueTestOutboxMessage(strategy);
    await strategy.FlushAsync(WorkBatchFlags.None, FlushMode.BestEffort);

    // Act
    await strategy.DisposeAsync();

    // Assert - dispose should flush remaining deferred items
    await Assert.That(coordinator.ProcessWorkBatchCallCount).IsEqualTo(1)
      .Because("DisposeAsync should flush remaining deferred messages");
  }

  [Test]
  public async Task Interval_CoalesceWindowMilliseconds_Default0_NoCoalescingAsync() {
    // Arrange
    var options = new WorkCoordinatorOptions { CoalesceWindowMilliseconds = 0 };

    // Assert
    await Assert.That(options.CoalesceWindowMilliseconds).IsEqualTo(0)
      .Because("default coalesce window should be 0 (no coalescing)");
  }

  [Test]
  public async Task Interval_CoalesceWindowMilliseconds_ConfigurableViaOptionsAsync() {
    // Arrange
    var options = new WorkCoordinatorOptions { CoalesceWindowMilliseconds = 50 };

    // Assert
    await Assert.That(options.CoalesceWindowMilliseconds).IsEqualTo(50);
  }

  // ========================================
  // Strategy Configuration
  // ========================================

  [Test]
  public async Task Strategy_Default_IsScopedAsync() {
    var options = new WorkCoordinatorOptions();
    await Assert.That(options.Strategy).IsEqualTo(WorkCoordinatorStrategy.Scoped);
  }

  [Test]
  public async Task Interval_DefaultIntervalMilliseconds_Is100Async() {
    var options = new WorkCoordinatorOptions();
    await Assert.That(options.IntervalMilliseconds).IsEqualTo(100);
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
      ProcessWorkBatchCallCount++;
      LastNewOutboxMessages = request.NewOutboxMessages;
      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCheckpointCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCheckpointFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCheckpointInfo?>(null);
  }

  private sealed class FakeWorkCoordinatorWithFlags : IWorkCoordinator {
    public WorkBatchFlags LastFlags { get; private set; }

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

    public Task ReportPerspectiveCompletionAsync(PerspectiveCheckpointCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCheckpointFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCheckpointInfo?>(null);
  }

  private sealed class FakeWorkCoordinatorWithReturnedWork : IWorkCoordinator {
    private readonly List<OutboxWork> _workToReturn;

    public FakeWorkCoordinatorWithReturnedWork(List<OutboxWork> workToReturn) {
      _workToReturn = workToReturn;
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(
      ProcessWorkBatchRequest request,
      CancellationToken cancellationToken = default) {
      return Task.FromResult(new WorkBatch {
        OutboxWork = _workToReturn,
        InboxWork = [],
        PerspectiveWork = []
      });
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCheckpointCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCheckpointFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCheckpointInfo?>(null);
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

  private sealed class FakeWorkChannelWriter : IWorkChannelWriter {
    public List<OutboxWork> WrittenWork { get; } = [];

    public System.Threading.Channels.ChannelReader<OutboxWork> Reader =>
      throw new System.NotImplementedException("Reader not needed for tests");

    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct) {
      WrittenWork.Add(work);
      return ValueTask.CompletedTask;
    }

    public bool TryWrite(OutboxWork work) {
      WrittenWork.Add(work);
      return true;
    }

    public void Complete() { }
  }

  private sealed class TestMessageEnvelope : IMessageEnvelope<JsonElement> {
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
