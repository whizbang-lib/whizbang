using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDispatchWorkerTests {

  // ============================================================
  // Test fakes
  // ============================================================

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 42;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class FakeInboxChannelWriter : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ConcurrentBag<Guid> RemovedInFlight { get; } = [];
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) => _channel.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { RemovedInFlight.Add(messageId); }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private sealed class FakeHandlerCommitChannel : IInboxHandlerCommitChannel {
    public TaskCompletionSource<HandlerCommitRequest> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentBag<HandlerCommitRequest> All { get; } = [];
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken ct = default) {
      All.Add(request);
      First.TrySetResult(request);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeFailureChannel : IFailureChannel {
    public TaskCompletionSource<MessageFailure> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentBag<(WorkCategory cat, MessageFailure f)> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add((category, failure));
      First.TrySetResult(failure);
      return ValueTask.CompletedTask;
    }
  }

  private static InboxWork _makeWork(int attempts = 0, MessageProcessingStatus status = MessageProcessingStatus.Stored, Guid? id = null) {
    var msgId = id ?? (Guid)TrackedGuid.NewMedo();
    var streamId = (Guid)TrackedGuid.NewMedo();
    return new InboxWork {
      MessageId = msgId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = streamId,
      PartitionNumber = 1,
      Attempts = attempts,
      Status = status,
      Flags = WorkBatchOptions.None,
    };
  }

  // Returns a fixed composite from DeserializeFromJsonElement so _resolveTypedEnvelope yields a
  // typed ICompositeEvent payload at the dispatch seam.
  private sealed class FakeCompositeDeserializer(ICompositeEvent composite) : ILifecycleMessageDeserializer {
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => composite;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => composite;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => composite;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => composite;
  }

  // Minimal serializer: records the inner payload's runtime AQN. Real JSON is covered elsewhere.
  private sealed class FakeEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var aqn = envelope.Payload!.GetType().AssemblyQualifiedName!;
      var jsonEnv = new MessageEnvelope<JsonElement> {
        DispatchContext = envelope.DispatchContext,
        MessageId = envelope.MessageId,
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = envelope.Hops?.ToList() ?? [],
      };
      return new SerializedEnvelope(jsonEnv, $"Whizbang.Core.Observability.MessageEnvelope`1[[{aqn}]], Whizbang.Core", aqn);
    }
    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
  }

  private sealed record _innerImportEvent(string Id) : IEvent;

  private sealed class _bulkComposite(params _innerImportEvent[] inner) : ICompositeEvent {
    public int MaxInnerEventsAllowed => 10_000;
    public IEnumerable<IMessage> InnerEvents => inner;
  }

  // ============================================================
  // Tests
  // ============================================================

  [Test]
  public async Task CompositeMessage_FansOutToChildInboxRowsAndDeletesCompositeAsync() {
    // A composite inbox row must fan out at the dispatch seam: the commit request carries one child
    // inbox message per inner event, and the composite row is marked EventStored (bit 2) so
    // process_inbox_completions DELETEs it. The composite itself is never persisted.
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var composite = new _bulkComposite(new _innerImportEvent("J-1"), new _innerImportEvent("J-2"), new _innerImportEvent("J-3"));
    var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .BuildServiceProvider();

    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions { PartitionCount = 7 }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      lifecycleMessageDeserializer: new FakeCompositeDeserializer(composite));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork();
    await inbox.WriteAsync(work, cts.Token);

    var routed = await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(routed.InboxCompletion.MessageId).IsEqualTo(work.MessageId);
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo((int)MessageProcessingStatus.EventStored)
      .Because("EventStored (bit 2) drives process_inbox_completions to DELETE the composite row in the same tx that stores the children.");
    await Assert.That(routed.NewInboxMessages).IsNotNull();
    await Assert.That(routed.NewInboxMessages!.Count).IsEqualTo(3)
      .Because("One child inbox message per inner event.");
    await Assert.That(routed.NewInboxMessages!.All(m => m.MessageType.Contains("_innerImportEvent", StringComparison.Ordinal))).IsTrue();
    await Assert.That(failure.All).IsEmpty();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  private sealed class FakeDeadLetterStore : IDeadLetterStore {
    public TaskCompletionSource<(Guid sourceId, MessageFailureReason reason)> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText, Guid instanceId, string generation, CancellationToken ct = default) {
      First.TrySetResult((sourceId, failureReason));
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  private sealed class FakeGenerationProvider : IGenerationProvider {
    public string GetGeneration() => "test-generation";
  }

  private sealed class _overCapComposite(int count) : ICompositeEvent {
    public int MaxInnerEventsAllowed => 1;
    public IEnumerable<IMessage> InnerEvents => Enumerable.Range(0, count).Select(i => (IMessage)new _innerImportEvent($"J-{i}"));
  }

  [Test]
  public async Task CompositeOverCap_DeadLettersCompositeRowAsync() {
    // A composite that yields more inner events than its cap must NOT partially fan out — it is an
    // inbox row that failed, so it dead-letters via the existing MoveAsync(wh_inbox) path (Phase 3 free).
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var dlq = new FakeDeadLetterStore();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var composite = new _overCapComposite(5); // cap = 1
    var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .BuildServiceProvider();

    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions { PartitionCount = 1 }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      lifecycleMessageDeserializer: new FakeCompositeDeserializer(composite),
      deadLetterStore: dlq,
      generationProvider: new FakeGenerationProvider());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork();
    await inbox.WriteAsync(work, cts.Token);

    var (sourceId, reason) = await dlq.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(sourceId).IsEqualTo(work.MessageId);
    await Assert.That(reason).IsEqualTo(MessageFailureReason.CompositeInnerEventLimitExceeded);
    // No children committed — cap breach is all-or-nothing.
    await Assert.That(handlerCommit.All.Any(r => r.NewInboxMessages is { Count: > 0 })).IsFalse();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // Registry that reports an inline receptor for every type at PostInboxInline — drives the
  // pre-fanout hook gate in _invokePreFanoutHookAsync.
  private sealed class PostInboxInlineRegistry : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => stage == LifecycleStage.PostInboxInline;
    public bool HasInboxHandler(string messageType) => true;
    public bool HasAnyConsumer(string messageType) => true;
  }

  // Simulates a pre-fanout IReceptor<TComposite> that emits a durable event: when fired inline, it
  // publishes via the ambient collector (the seam Dispatcher.PublishToOutboxAsync uses).
  private sealed class EmittingInvoker(OutboxMessage emitted) : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      if (stage == LifecycleStage.PostInboxInline) {
        DispatchOutboxCollector.Current?.Add(emitted);
      }
      return ValueTask.CompletedTask;
    }
  }

  private static OutboxMessage _outboxMsg(string id) => new() {
    MessageId = (Guid)TrackedGuid.NewMedo(),
    Envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse($"{{\"id\":\"{id}\"}}").RootElement,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    },
    Metadata = new EnvelopeMetadata { MessageId = MessageId.New(), Hops = [] },
    EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.BatchReceived, TestApp]], Whizbang.Core",
    MessageType = "TestApp.BatchReceived, TestApp",
  };

  // A pre-fanout receptor that imposes a fan-out directive via the ambient control.
  private sealed class DirectiveInvoker(FanoutDirective directive) : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      if (stage == LifecycleStage.PostInboxInline) {
        DispatchFanoutControl.Set(directive);
      }
      return ValueTask.CompletedTask;
    }
  }

  // Composite that declares FanoutMode.Manual.
  private sealed class _manualComposite(params _innerImportEvent[] inner) : ICompositeEvent {
    public int MaxInnerEventsAllowed => 10_000;
    public FanoutMode FanoutMode => FanoutMode.Manual;
    public IEnumerable<IMessage> InnerEvents => inner;
  }

  private async Task<HandlerCommitRequest> _runCompositeWithInvokerAsync(
      ICompositeEvent composite, IReceptorInvoker invoker) {
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .AddSingleton(invoker)
      .BuildServiceProvider();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeInstanceProvider(), inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions { PartitionCount = 1 }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      lifecycleMessageDeserializer: new FakeCompositeDeserializer(composite),
      receptorRegistry: new PostInboxInlineRegistry());
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await inbox.WriteAsync(_makeWork(), cts.Token);
    var routed = await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
    return routed;
  }

  [Test]
  public async Task CompositeDirective_Skip_CommitsNoChildren_DeletesCompositeAsync() {
    var composite = new _bulkComposite(new _innerImportEvent("J-1"), new _innerImportEvent("J-2"));
    var routed = await _runCompositeWithInvokerAsync(composite, new DirectiveInvoker(FanoutDirective.Skip));

    await Assert.That(routed.NewInboxMessages is null || routed.NewInboxMessages.Count == 0).IsTrue()
      .Because("Skip suppresses fan-out — no children are created.");
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo((int)MessageProcessingStatus.EventStored)
      .Because("The composite row is still deleted (EventStored bit) — the receptor handled it.");
  }

  [Test]
  public async Task CompositeDirective_ReplaceWith_FansOutReplacementSetAsync() {
    var composite = new _bulkComposite(new _innerImportEvent("original"));
    var replacement = new IMessage[] { new _innerImportEvent("R-1"), new _innerImportEvent("R-2"), new _innerImportEvent("R-3") };
    var routed = await _runCompositeWithInvokerAsync(composite, new DirectiveInvoker(FanoutDirective.ReplaceWith(replacement)));

    await Assert.That(routed.NewInboxMessages!.Count).IsEqualTo(3)
      .Because("ReplaceWith fans out the receptor-supplied 3, not the composite's own 1 inner event.");
  }

  [Test]
  public async Task CompositeFanoutMode_Manual_NoReceptorDirective_FansOutNothingAsync() {
    var composite = new _manualComposite(new _innerImportEvent("J-1"), new _innerImportEvent("J-2"));
    // Invoker present (so the gate opens) but sets no directive.
    var routed = await _runCompositeWithInvokerAsync(composite, new DirectiveInvoker(FanoutDirective.Proceed));

    // Proceed + Manual → nothing auto-fans-out (the receptor didn't drive it).
    await Assert.That(routed.NewInboxMessages is null || routed.NewInboxMessages.Count == 0).IsTrue()
      .Because("Manual mode does not auto-fan-out; without a ReplaceWith directive, no children are produced.");
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo((int)MessageProcessingStatus.EventStored);
  }

  [Test]
  public async Task CompositeWithPreFanoutReceptor_CommitsEmittedEventAtomicallyWithChildrenAsync() {
    // Phase B: a pre-fanout receptor's emitted event (captured via the ambient collector) and the
    // fan-out children must land in ONE HandlerCommitRequest — pre-fanout side-effects + children
    // commit all-or-nothing.
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var emitted = _outboxMsg("batch-received");
    var composite = new _bulkComposite(new _innerImportEvent("J-1"), new _innerImportEvent("J-2"));
    var sp = new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer())
      .AddSingleton<IReceptorInvoker>(new EmittingInvoker(emitted))
      .BuildServiceProvider();

    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions { PartitionCount = 1 }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      lifecycleMessageDeserializer: new FakeCompositeDeserializer(composite),
      receptorRegistry: new PostInboxInlineRegistry());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await inbox.WriteAsync(_makeWork(), cts.Token);

    var routed = await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(routed.NewInboxMessages!.Count).IsEqualTo(2)
      .Because("Both fan-out children are in the request.");
    await Assert.That(routed.NewOutboxMessages).IsNotNull();
    await Assert.That(routed.NewOutboxMessages!.Count).IsEqualTo(1)
      .Because("The pre-fanout receptor's emitted event rides the SAME commit request as the children.");
    await Assert.That(routed.NewOutboxMessages![0].MessageId).IsEqualTo(emitted.MessageId);
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo((int)MessageProcessingStatus.EventStored);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task HappyPath_RoutesEventStoredCompletionToHandlerCommitChannelAsync() {
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions { PartitionCount = 1234 }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork();
    await inbox.WriteAsync(work, cts.Token);

    var routed = await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(routed.InboxCompletion.MessageId).IsEqualTo(work.MessageId);
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo((int)MessageProcessingStatus.EventStored);
    await Assert.That(routed.InstanceId).IsEqualTo(instance.InstanceId);
    await Assert.That(routed.ServiceName).IsEqualTo("test-svc");
    await Assert.That(routed.HostName).IsEqualTo("test-host");
    await Assert.That(routed.ProcessId).IsEqualTo(42);
    await Assert.That(routed.PartitionCount).IsEqualTo(1234);
    await Assert.That(routed.HandlerId).IsEqualTo(work.MessageId);
    await Assert.That(failure.All).IsEmpty();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task MaxInboxAttemptsExceeded_RoutesTerminalCompletionAsync() {
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions { MaxInboxAttempts = 3 }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork(attempts: 5, status: MessageProcessingStatus.Stored);
    await inbox.WriteAsync(work, cts.Token);

    var routed = await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(routed.InboxCompletion.MessageId).IsEqualTo(work.MessageId);
    var expectedStatus = (int)(work.Status | MessageProcessingStatus.Published);
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo(expectedStatus);
    await Assert.That(failure.All).IsEmpty();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task MaxInboxAttempts_AttemptsEqualToMax_StillProcessesAsync() {
    // Phase H step 8 slice D regression lock: with one-based attempts (first attempt = 1)
    // and N total attempts allowed, the dead-letter check must use strict greater-than.
    // attempts=3, MaxInboxAttempts=3 → run the 3rd attempt (don't dead-letter early).
    // Pre-refactor used >= with zero-based attempts which gave the same total-attempts count.
    // Flipping the operator preserves "MaxInboxAttempts = N → N attempts allowed".
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions { MaxInboxAttempts = 3 }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork(attempts: 3, status: MessageProcessingStatus.Stored);
    await inbox.WriteAsync(work, cts.Token);

    var routed = await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    // Should be normal EventStored, NOT terminal Published — dead-letter only fires when
    // attempts > max, not when equal.
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo((int)MessageProcessingStatus.EventStored)
      .Because("attempts == MaxInboxAttempts means we're on the last allowed attempt, not over the limit");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task MaxInboxAttempts_AttemptsOneOverMax_DeadLettersAsync() {
    // Counterpart to the boundary test: attempts > max strictly triggers dead-letter.
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions { MaxInboxAttempts = 3 }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork(attempts: 4, status: MessageProcessingStatus.Stored);
    await inbox.WriteAsync(work, cts.Token);

    var routed = await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var expectedStatus = (int)(work.Status | MessageProcessingStatus.Published);
    await Assert.That(routed.InboxCompletion.Status).IsEqualTo(expectedStatus)
      .Because("attempts > MaxInboxAttempts dead-letters with terminal Published status");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Disabled_NoMessagesConsumedAsync() {
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();  // gate ready, but Enabled=false should still skip

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions { Enabled = false }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork();
    await inbox.WriteAsync(work, cts.Token);

    await Task.WhenAny(handlerCommit.First.Task, Task.Delay(500, CancellationToken.None));
    await Assert.That(handlerCommit.First.Task.IsCompleted).IsFalse();
    await Assert.That(handlerCommit.All).IsEmpty();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task BlocksOnSchemaGate_UntilMarkedReadyAsync() {
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();  // not marked ready

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork();
    await inbox.WriteAsync(work, cts.Token);

    await Task.WhenAny(handlerCommit.First.Task, Task.Delay(300, CancellationToken.None));
    await Assert.That(handlerCommit.First.Task.IsCompleted).IsFalse();

    gate.MarkReady();
    var routed = await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(routed.InboxCompletion.MessageId).IsEqualTo(work.MessageId);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task MultipleMessages_AllRoutedToHandlerCommitChannelAsync() {
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var works = new[] { _makeWork(), _makeWork(), _makeWork() };
    foreach (var w in works) { await inbox.WriteAsync(w, cts.Token); }

    await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (handlerCommit.All.Count < works.Length && sw.Elapsed < TimeSpan.FromSeconds(2)) {
      await Task.Yield();
    }
    foreach (var w in works) {
      await Assert.That(handlerCommit.All.Any(r => r.InboxCompletion.MessageId == w.MessageId)).IsTrue();
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Phase H step 9 slice 3 — lease-tied cancellation
  // ============================================================

  /// <summary>
  /// HandlerCommit channel that ignores its CT — simulates a misbehaving downstream that doesn't
  /// honor cancellation. Used to verify the lease executor's abandonment path.
  /// </summary>
  private sealed class HungHandlerCommitChannel : IInboxHandlerCommitChannel {
    private readonly TaskCompletionSource _block = new();
    public Task Started => _entered.Task;
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken ct = default) {
      _entered.TrySetResult();
      // Deliberately ignore ct — simulates a hung downstream/handler.
      await _block.Task.ConfigureAwait(false);
    }
    public void Unblock() => _block.TrySetResult();
  }

  [Test]
  public async Task HungInsideLeaseExecutor_CancelsAtDeadline_RoutesToFailureChannelAsync() {
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var hungChannel = new HungHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero));
    var registry = new LeaseRegistry();

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, hungChannel, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      lifecycleMessageDeserializer: null,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions { LeaseGraceSeconds = 30, MaxRenewalsPerWork = 6 }),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions { LeaseSeconds = 60 }),
      leaseRegistry: registry,
      timeProvider: fakeTime);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork();
    await inbox.WriteAsync(work, cts.Token);

    // Wait until the dispatch enters the hung EnqueueAsync (so we know the lease has been
    // registered and the executor is awaiting the dispatch task).
    await hungChannel.Started.WaitAsync(TimeSpan.FromSeconds(5));

    // Advance fake clock past deadline (60 - 30 = 30 s grace, deadline at +30 s).
    fakeTime.Advance(TimeSpan.FromSeconds(31));

    // The lease executor should abandon the hung dispatch and route to the failure channel
    // via the existing catch in ExecuteAsync.
    var fail = await failure.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(fail.MessageId).IsEqualTo(work.MessageId);

    // The hung downstream is still parked — abandon-not-cancel.
    hungChannel.Unblock();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SuccessfulDispatch_DisposesLeaseAndRemovesFromRegistryAsync() {
    var instance = new FakeInstanceProvider();
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var failure = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var registry = new LeaseRegistry();

    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, inbox, handlerCommit, failure, gate,
      Options.Create(new InboxDispatchWorkerOptions()),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      lifecycleMessageDeserializer: null,
      leaseHandleOptions: null,
      leaseRenewalOptions: null,
      leaseRegistry: registry,
      timeProvider: null);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var work = _makeWork();
    await inbox.WriteAsync(work, cts.Token);

    await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
    // Allow dispatch to fully complete + lease.Dispose continuation to run.
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (registry.Count > 0 && sw.Elapsed < TimeSpan.FromSeconds(2)) {
      await Task.Yield();
    }

    await Assert.That(registry.Count).IsEqualTo(0)
      .Because("happy-path dispatch must dispose the lease (auto-removes from registry)");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }
}
