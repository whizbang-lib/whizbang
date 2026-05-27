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
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Direct coverage for <see cref="WorkCoordinatorFlushHelper.ExecuteFlushAsync"/> — the single
/// distribution path that the four strategies (Immediate / Interval / Batch / Scoped) all flow
/// through. Locks the invariants that strategy-level tests used to assert against the legacy
/// <c>ProcessWorkBatchAsync</c> contract.
/// </summary>
/// <docs>data/work-coordinator-strategies</docs>
public class WorkCoordinatorFlushHelperTests {

  private const string STRATEGY_NAME = "test";

  [Test]
  public async Task EmptyQueues_ReturnsEmptyBatch_DoesNotInvokeCoordinatorAsync() {
    var coordinator = new CapturingWorkCoordinator();
    var batch = await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(coordinator: coordinator), default);

    await Assert.That(coordinator.StoreOutboxCallCount).IsEqualTo(0);
    await Assert.That(coordinator.StoreInboxCallCount).IsEqualTo(0);
    await Assert.That(batch.OutboxWork.Count).IsEqualTo(0);
    await Assert.That(batch.InboxWork.Count).IsEqualTo(0);
  }

  [Test]
  public async Task OutboxMessages_RouteToStoreOutboxMessagesAsync() {
    var coordinator = new CapturingWorkCoordinator();
    var messages = new[] { _outbox(), _outbox() };

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(coordinator: coordinator, outboxMessages: messages), default);

    await Assert.That(coordinator.StoreOutboxCallCount).IsEqualTo(1);
    await Assert.That(coordinator.LastStoredOutbox.Length).IsEqualTo(2);
    await Assert.That(coordinator.StoreInboxCallCount).IsEqualTo(0);
  }

  [Test]
  public async Task InboxMessages_RouteToStoreInboxMessagesAsync() {
    var coordinator = new CapturingWorkCoordinator();
    var messages = new[] { _inbox() };

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(coordinator: coordinator, inboxMessages: messages), default);

    await Assert.That(coordinator.StoreInboxCallCount).IsEqualTo(1);
    await Assert.That(coordinator.LastStoredInbox.Length).IsEqualTo(1);
    await Assert.That(coordinator.StoreOutboxCallCount).IsEqualTo(0);
  }

  [Test]
  public async Task PendingAuditMessages_MergedIntoOutboxBeforeStoreAsync() {
    var coordinator = new CapturingWorkCoordinator();
    var business = new[] { _outbox(), _outbox() };
    var audit = new[] { _outbox() };

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(coordinator: coordinator, outboxMessages: business, pendingAudit: audit), default);

    await Assert.That(coordinator.StoreOutboxCallCount).IsEqualTo(1);
    await Assert.That(coordinator.LastStoredOutbox.Length).IsEqualTo(3)
      .Because("audit messages append to business outbox in a single store call");
  }

  [Test]
  public async Task PendingAuditMessages_OnlyAudit_AllOtherQueuesEmpty_ShortCircuitsAsync() {
    // Documents the current short-circuit: empty-queue check happens BEFORE audit merge.
    // Audit-only flushes need at least one business message or completion/failure to land.
    // Strategies in practice always batch audit alongside business work, so this isn't observed
    // in production — but if that ever changes, the empty-queue check needs to include audit.
    var coordinator = new CapturingWorkCoordinator();
    var audit = new[] { _outbox() };

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(coordinator: coordinator, outboxMessages: [], pendingAudit: audit), default);

    await Assert.That(coordinator.StoreOutboxCallCount).IsEqualTo(0);
  }

  [Test]
  public async Task OutboxMessages_SignalsNewWorkAvailableAsync() {
    var coordinator = new CapturingWorkCoordinator();
    var writer = new CountingWorkChannelWriter();

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(coordinator: coordinator, outboxMessages: [_outbox()], workChannelWriter: writer),
      default);

    await Assert.That(writer.SignalCount).IsEqualTo(1)
      .Because("ClaimWorker subscribes to OnNewWorkAvailable to skip the next poll wait");
  }

  [Test]
  public async Task NoOutboxMessages_DoesNotSignalAsync() {
    var coordinator = new CapturingWorkCoordinator();
    var writer = new CountingWorkChannelWriter();

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(
        coordinator: coordinator,
        outboxMessages: [],
        outboxCompletions: [_completion()],
        workChannelWriter: writer),
      default);

    await Assert.That(writer.SignalCount).IsEqualTo(0)
      .Because("Signal is only useful when fresh outbox rows landed; completions don't trigger it");
  }

  [Test]
  public async Task DirectCoordinator_NoScope_CompletionsAreDroppedSilentlyAsync() {
    // Documents the post-Phase-H contract: completions only flow to IOutboxCompletionChannel
    // when a scoped provider is available. Strategies that pass a direct coordinator (no scope)
    // get no completion routing — by design.
    var coordinator = new CapturingWorkCoordinator();
    var completionChannel = new CountingOutboxCompletionChannel();

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(coordinator: coordinator, outboxCompletions: [_completion(), _completion()]),
      default);

    await Assert.That(completionChannel.EnqueueCount).IsEqualTo(0);
  }

  [Test]
  public async Task ScopePath_OutboxCompletions_RouteToCompletionChannelAsync() {
    var completionChannel = new CountingOutboxCompletionChannel();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(new CapturingWorkCoordinator());
    services.AddSingleton<IOutboxCompletionChannel>(completionChannel);
    using var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(
        coordinator: null,
        scopeFactory: scopeFactory,
        outboxCompletions: [_completion(), _completion(), _completion()]),
      default);

    await Assert.That(completionChannel.EnqueueCount).IsEqualTo(3);
  }

  [Test]
  public async Task ScopePath_OutboxFailures_RouteToFailureChannelWithOutboxCategoryAsync() {
    var failureChannel = new CountingFailureChannel();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(new CapturingWorkCoordinator());
    services.AddSingleton<IFailureChannel>(failureChannel);
    using var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(
        coordinator: null,
        scopeFactory: scopeFactory,
        outboxFailures: [_failure(), _failure()]),
      default);

    await Assert.That(failureChannel.EnqueueCount).IsEqualTo(2);
    await Assert.That(failureChannel.Categories[0]).IsEqualTo(WorkCategory.Outbox);
    await Assert.That(failureChannel.Categories[1]).IsEqualTo(WorkCategory.Outbox);
  }

  [Test]
  public async Task ScopePath_InboxFailures_RouteToFailureChannelWithInboxCategoryAsync() {
    var failureChannel = new CountingFailureChannel();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(new CapturingWorkCoordinator());
    services.AddSingleton<IFailureChannel>(failureChannel);
    using var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(
        coordinator: null,
        scopeFactory: scopeFactory,
        inboxFailures: [_failure()]),
      default);

    await Assert.That(failureChannel.EnqueueCount).IsEqualTo(1);
    await Assert.That(failureChannel.Categories[0]).IsEqualTo(WorkCategory.Inbox);
  }

  [Test]
  public async Task ScopePath_InboxMessages_SignalsInboxChannelWriterAsync() {
    var inboxWriter = new CountingInboxChannelWriter();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(new CapturingWorkCoordinator());
    services.AddSingleton<IInboxChannelWriter>(inboxWriter);
    using var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(
        coordinator: null,
        scopeFactory: scopeFactory,
        inboxMessages: [_inbox()]),
      default);

    await Assert.That(inboxWriter.SignalCount).IsEqualTo(1);
  }

  [Test]
  public async Task ScopePath_NoCompletionChannelRegistered_DoesNotThrowAsync() {
    // The helper soft-resolves channels (GetService, not GetRequiredService); a host that
    // doesn't register them must not crash the flush path.
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(new CapturingWorkCoordinator());
    using var sp = services.BuildServiceProvider();

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(
        coordinator: null,
        scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
        outboxCompletions: [_completion()],
        outboxFailures: [_failure()]),
      default);

    // No assertion needed beyond "didn't throw"; reaching this line is the success condition.
  }

  [Test]
  public async Task NeitherCoordinatorNorScopeFactory_ThrowsInvalidOperationAsync() {
    await Assert.That(async () =>
      await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
        _ctx(coordinator: null, scopeFactory: null, outboxMessages: [_outbox()]), default))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task PartitionCount_ClaimWorkerOptions_TakesPrecedenceAsync() {
    var coordinator = new CapturingWorkCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddOptions<ClaimWorkerOptions>().Configure(o => o.PartitionCount = 42);
    using var sp = services.BuildServiceProvider();

    var options = new WorkCoordinatorOptions { PartitionCount = 9999 };

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(
        coordinator: null,
        scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
        options: options,
        outboxMessages: [_outbox()]),
      default);

    await Assert.That(coordinator.LastStoredPartitionCount).IsEqualTo(42)
      .Because("ClaimWorkerOptions.PartitionCount overrides WorkCoordinatorOptions when present");
  }

  [Test]
  public async Task PartitionCount_FallbackToWorkCoordinatorOptionsAsync() {
    var coordinator = new CapturingWorkCoordinator();
    var options = new WorkCoordinatorOptions { PartitionCount = 7 };

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(coordinator: coordinator, options: options, outboxMessages: [_outbox()]),
      default);

    await Assert.That(coordinator.LastStoredPartitionCount).IsEqualTo(7);
  }

  [Test]
  public async Task SkipLifecycle_True_DoesNotInvokeLifecycleStagesAsync() {
    // Doesn't require a fake lifecycle invoker — null lifecycle context is the no-op case.
    // The behavior under test is "no NullReferenceException from LifecycleInvocationHelper
    // when SkipLifecycle is true and no deserializer/metrics are provided."
    var coordinator = new CapturingWorkCoordinator();

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(coordinator: coordinator, outboxMessages: [_outbox()], skipLifecycle: true),
      default);

    await Assert.That(coordinator.StoreOutboxCallCount).IsEqualTo(1)
      .Because("data path still runs even when lifecycle stages are skipped");
  }

  [Test]
  public async Task CombinedFlush_OutboxInboxCompletionsFailures_AllRouteAsync() {
    var coordinator = new CapturingWorkCoordinator();
    var completionChannel = new CountingOutboxCompletionChannel();
    var failureChannel = new CountingFailureChannel();
    var workWriter = new CountingWorkChannelWriter();
    var inboxWriter = new CountingInboxChannelWriter();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IOutboxCompletionChannel>(completionChannel);
    services.AddSingleton<IFailureChannel>(failureChannel);
    services.AddSingleton<IInboxChannelWriter>(inboxWriter);
    using var sp = services.BuildServiceProvider();

    await WorkCoordinatorFlushHelper.ExecuteFlushAsync(
      _ctx(
        coordinator: null,
        scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
        outboxMessages: [_outbox()],
        inboxMessages: [_inbox()],
        outboxCompletions: [_completion(), _completion()],
        outboxFailures: [_failure()],
        inboxFailures: [_failure(), _failure(), _failure()],
        workChannelWriter: workWriter),
      default);

    await Assert.That(coordinator.StoreOutboxCallCount).IsEqualTo(1);
    await Assert.That(coordinator.StoreInboxCallCount).IsEqualTo(1);
    await Assert.That(completionChannel.EnqueueCount).IsEqualTo(2);
    await Assert.That(failureChannel.EnqueueCount).IsEqualTo(4);
    await Assert.That(workWriter.SignalCount).IsEqualTo(1);
    await Assert.That(inboxWriter.SignalCount).IsEqualTo(1);
  }

  // ===== Helpers =====

  private static FlushContext _ctx(
    IWorkCoordinator? coordinator = null,
    IServiceScopeFactory? scopeFactory = null,
    WorkCoordinatorOptions? options = null,
    OutboxMessage[]? outboxMessages = null,
    InboxMessage[]? inboxMessages = null,
    MessageCompletion[]? outboxCompletions = null,
    MessageCompletion[]? inboxCompletions = null,
    MessageFailure[]? outboxFailures = null,
    MessageFailure[]? inboxFailures = null,
    IWorkChannelWriter? workChannelWriter = null,
    OutboxMessage[]? pendingAudit = null,
    bool skipLifecycle = true
  ) => new(
    coordinator,
    scopeFactory,
    new FakeInstanceProvider(),
    options ?? new WorkCoordinatorOptions { PartitionCount = 10 },
    STRATEGY_NAME,
    outboxMessages ?? [],
    inboxMessages ?? [],
    outboxCompletions ?? [],
    inboxCompletions ?? [],
    outboxFailures ?? [],
    inboxFailures ?? [],
    WorkBatchOptions.None,
    LifecycleMessageDeserializer: null,
    Logger: null,
    TracingOptions: null,
    Metrics: null,
    LifecycleMetrics: null,
    WorkChannelWriter: workChannelWriter,
    PendingAuditMessages: pendingAudit,
    SkipLifecycle: skipLifecycle);

  private static OutboxMessage _outbox() {
    var id = Guid.CreateVersion7();
    return new OutboxMessage {
      MessageId = id,
      Destination = "test-topic",
      Envelope = _envelope(id),
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      StreamId = Guid.CreateVersion7(),
      IsEvent = true,
      MessageType = "TestMessage, TestAssembly",
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(id),
        Hops = []
      }
    };
  }

  private static InboxMessage _inbox() {
    var id = Guid.CreateVersion7();
    return new InboxMessage {
      MessageId = id,
      HandlerName = "TestHandler",
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Object, System.Private.CoreLib]], Whizbang.Core",
      Envelope = _envelope(id),
      MessageType = "TestMessage, TestAssembly",
      StreamId = Guid.CreateVersion7(),
      IsEvent = true,
      Metadata = new EnvelopeMetadata {
        MessageId = MessageId.From(id),
        Hops = []
      }
    };
  }

  private static MessageCompletion _completion() => new() {
    MessageId = Guid.CreateVersion7(),
    Status = MessageProcessingStatus.Published
  };

  private static MessageFailure _failure() => new() {
    MessageId = Guid.CreateVersion7(),
    CompletedStatus = MessageProcessingStatus.None,
    Error = "test-error"
  };

  private static TestEnvelope _envelope(Guid id) => new() {
    MessageId = MessageId.From(id),
    Hops = []
  };

  private sealed class TestEnvelope : IMessageEnvelope<JsonElement> {
    public int Version => 1;
    public MessageDispatchContext DispatchContext { get; } = new() { Mode = DispatchModes.Local, Source = MessageSource.Local };
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

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public string ServiceName => "TestService";
    public string HostName => "test-host";
    public int ProcessId => 12345;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class CapturingWorkCoordinator : IWorkCoordinator {
    public int StoreOutboxCallCount { get; private set; }
    public int StoreInboxCallCount { get; private set; }
    public OutboxMessage[] LastStoredOutbox { get; private set; } = [];
    public InboxMessage[] LastStoredInbox { get; private set; } = [];
    public int LastStoredPartitionCount { get; private set; }

    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) {
      StoreOutboxCallCount++;
      LastStoredOutbox = messages;
      LastStoredPartitionCount = partitionCount;
      return Task.CompletedTask;
    }

    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) {
      StoreInboxCallCount++;
      LastStoredInbox = messages;
      LastStoredPartitionCount = partitionCount;
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class CountingOutboxCompletionChannel : IOutboxCompletionChannel {
    public int EnqueueCount;
    public ConcurrentQueue<Guid> Ids { get; } = new();

    public ValueTask EnqueueAsync(Guid outboxMessageId, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref EnqueueCount);
      Ids.Enqueue(outboxMessageId);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class CountingFailureChannel : IFailureChannel {
    public int EnqueueCount;
    public List<WorkCategory> Categories { get; } = [];
    private readonly object _lock = new();

    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      lock (_lock) {
        EnqueueCount++;
        Categories.Add(category);
      }
      return ValueTask.CompletedTask;
    }
  }

  private sealed class CountingWorkChannelWriter : IWorkChannelWriter {
    public int SignalCount;
    public ChannelReader<OutboxWork> Reader => throw new NotSupportedException();
    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct = default) => ValueTask.CompletedTask;
    public bool TryWrite(OutboxWork work) => true;
    public void Complete() { }
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public void ClearInFlight() { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public event Action? OnNewWorkAvailable;
    public void SignalNewWorkAvailable() { Interlocked.Increment(ref SignalCount); OnNewWorkAvailable?.Invoke(); }
    public event Action? OnNewPerspectiveWorkAvailable;
    public void SignalNewPerspectiveWorkAvailable() => OnNewPerspectiveWorkAvailable?.Invoke();
  }

  private sealed class CountingInboxChannelWriter : IInboxChannelWriter {
    public int SignalCount;
    public ChannelReader<InboxWork> Reader => throw new NotSupportedException();
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => ValueTask.CompletedTask;
    public bool TryWrite(InboxWork work) => true;
    public void Complete() { }
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() { Interlocked.Increment(ref SignalCount); OnNewInboxWorkAvailable?.Invoke(); }
  }
}
