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
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A composite is expanded and committed as one step (#737). The dispatcher used to hand the expansion to
/// the batched commit channel and return; until that commit landed the composite row stayed leased and
/// unprocessed, the claim loop re-offered it on every poll, and the dispatcher expanded it again with
/// fresh child ids. Measured: fifteen copies of every inner event. Now the dispatcher commits the composite
/// through the coordinator before it returns, so a composite is never in the "expanded but not committed"
/// state the claim loop can see, and its children never sit in memory between dispatch and commit (#740).
/// The composite meter (#738) counts what happened.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#transactional-expansion</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class InboxDispatchWorkerCompositeCommitTests {

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 42;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class FakeInboxChannelWriter : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _removed = new();
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) => _channel.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) => _signal(messageId).TrySetResult();
    /// <summary>Completes when the worker releases <paramref name="messageId"/>; a signal the body emits, never a poll.</summary>
    public Task Released(Guid messageId) => _signal(messageId).Task;
    private TaskCompletionSource _signal(Guid messageId) =>
      _removed.GetOrAdd(messageId, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private sealed class FakeHandlerCommitChannel : IInboxHandlerCommitChannel {
    public ConcurrentBag<HandlerCommitRequest> All { get; } = [];
    public TaskCompletionSource<HandlerCommitRequest> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken ct = default) {
      All.Add(request);
      First.TrySetResult(request);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeFailureChannel : IFailureChannel {
    public ConcurrentBag<(WorkCategory Category, MessageFailure Failure)> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add((category, failure));
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Records every composite commit; can hold the first one open or refuse it.</summary>
  private sealed class RecordingCoordinator : IWorkCoordinator {
    private readonly ConcurrentDictionary<int, TaskCompletionSource<HandlerCommitRequest>> _commits = new();
    public Exception? FailWith { get; set; }
    private int _calls;
    private int _committed;

    public Task CommitHandlerResultAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) {
      var call = Interlocked.Increment(ref _calls);
      if (FailWith is not null && call == 1) {
        throw FailWith;
      }
      var ordinal = Interlocked.Increment(ref _committed);
      _signal(ordinal).TrySetResult(request);
      return Task.CompletedTask;
    }

    /// <summary>Completes with the <paramref name="ordinal"/>th successful commit (1-based); a signal the body emits, never a poll.</summary>
    public Task<HandlerCommitRequest> Commit(int ordinal) => _signal(ordinal).Task;
    public int CommittedCount => Volatile.Read(ref _committed);
    private TaskCompletionSource<HandlerCommitRequest> _signal(int ordinal) =>
      _commits.GetOrAdd(ordinal, static _ => new TaskCompletionSource<HandlerCommitRequest>(TaskCreationOptions.RunContinuationsAsynchronously));

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class FakeCompositeDeserializer(ICompositeEvent composite) : ILifecycleMessageDeserializer {
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => composite;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => composite;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => composite;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => composite;
  }

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
    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) => throw new NotSupportedException();
  }

  /// <summary>Discards children of one type at the inbox gate (the consumer never subscribed to it); keeps the composite itself.</summary>
  private sealed class DiscardOneTypePolicy(string discardedTypeFragment) : IMessageDiscardPolicy {
    public MessageDiscardDecision EvaluateReceive(string payloadClrType, string topic, string subscription) => new(false, MessageDiscardReason.None);
    public MessageDiscardDecision EvaluateInbox(string payloadClrType) =>
      payloadClrType.Contains(discardedTypeFragment, StringComparison.Ordinal)
        ? new(true, MessageDiscardReason.RegistryChanged, "no consumer registered")
        : new(false, MessageDiscardReason.None);
    public MessageDiscardDecision EvaluateOutbox(string payloadClrType) => new(false, MessageDiscardReason.None);
    public void RecordDiscard(MessageDiscardGate gate, MessageDiscardDecision decision, string payloadClrType, IReadOnlyDictionary<string, object?>? additionalTags = null) { }
  }

  private sealed record _rowAdded(string Id) : IEvent;
  private sealed record _rowRemoved(string Id) : IEvent;
  private sealed class _composite(params IMessage[] inner) : ICompositeEvent {
    public IEnumerable<IMessage> InnerEvents => inner;
  }

  private static InboxWork _compositeWork() {
    var msgId = (Guid)TrackedGuid.NewMedo();
    return new InboxWork {
      MessageId = msgId,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox }
      },
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = (Guid)TrackedGuid.NewMedo(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  private sealed record Harness(
    InboxDispatchWorker Worker, FakeInboxChannelWriter Inbox, FakeHandlerCommitChannel CommitChannel,
    FakeFailureChannel Failures, RecordingCoordinator Coordinator, CompositeMetrics Metrics, TestMeterFactory MeterFactory) : IDisposable {
    public void Dispose() => MeterFactory.Dispose();
  }

  private static Harness _harness(ICompositeEvent composite, IMessageDiscardPolicy? discardPolicy = null, Exception? commitFailure = null, bool withCoordinator = true) {
    var coordinator = new RecordingCoordinator { FailWith = commitFailure };
    var services = new ServiceCollection().AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer());
    if (withCoordinator) {
      services.AddScoped<IWorkCoordinator>(_ => coordinator);
    }
    var sp = services.BuildServiceProvider();
    var factory = new TestMeterFactory();
    var metrics = new CompositeMetrics(new WhizbangMetrics(factory));
    var inbox = new FakeInboxChannelWriter();
    var commitChannel = new FakeHandlerCommitChannel();
    var failures = new FakeFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new InboxDispatchWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new FakeInstanceProvider(), inbox, commitChannel, failures, gate,
      Options.Create(new InboxDispatchWorkerOptions { PartitionCount = 7 }),
      Options.Create(new WorkCoordinatorOptions()),
      NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new StreamIntegrityOptions()),
      lifecycleMessageDeserializer: new FakeCompositeDeserializer(composite),
      discardPolicy: discardPolicy,
      compositeMetrics: metrics);
    return new Harness(worker, inbox, commitChannel, failures, coordinator, metrics, factory);
  }

  [Test]
  public async Task Composite_IsExpandedAndCommittedInOneStep_ThroughTheCoordinator_NeverTheCommitChannelAsync() {
    using var h = _harness(new _composite(new _rowAdded("a"), new _rowAdded("b"), new _rowRemoved("c")));
    using var cts = new CancellationTokenSource();
    await h.Worker.StartAsync(cts.Token);

    var work = _compositeWork();
    await h.Inbox.WriteAsync(work, cts.Token);
    var commit = await h.Coordinator.Commit(1).WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(commit.InboxCompletion.MessageId).IsEqualTo(work.MessageId);
    await Assert.That(commit.InboxCompletion.Status).IsEqualTo((int)MessageProcessingStatus.EventStored)
      .Because("the same commit marks the composite done (deleted) and stores its children");
    await Assert.That(commit.NewInboxMessages!.Count).IsEqualTo(3);
    await Assert.That(h.CommitChannel.All).IsEmpty()
      .Because("a composite's expansion never waits in the batched channel; that queue is where a re-offer found it still pending (#737, #740)");

    // The row is released only after the commit landed, so a re-offer arriving mid-commit is filtered.
    await h.Inbox.Released(work.MessageId).WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(h.Coordinator.CommittedCount).IsEqualTo(1).Because("once committed the composite row is gone and its in-flight entry goes with it; the release follows the commit");
    await Assert.That(h.Failures.All).IsEmpty();

    await cts.CancelAsync();
    await h.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Composite_WithNoCoordinatorInTheScope_FallsBackToTheCommitChannelAsync() {
    // A host that dispatches without a coordinator in scope (nothing the framework registers, but a
    // possible test or custom host) keeps the batched path rather than losing the composite; the
    // warning names what that costs (#737). The in-flight entry is kept, as the batched path always did,
    // so a re-offer while the commit waits is filtered rather than re-expanded.
    using var h = _harness(new _composite(new _rowAdded("a")), withCoordinator: false);
    using var cts = new CancellationTokenSource();
    await h.Worker.StartAsync(cts.Token);

    var work = _compositeWork();
    await h.Inbox.WriteAsync(work, cts.Token);
    var queued = await h.CommitChannel.First.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(queued.NewInboxMessages!.Count).IsEqualTo(1)
      .Because("with no coordinator to commit through, the expansion still has to land somewhere durable; the channel is the fallback, never a drop");
    await Assert.That(h.CommitChannel.All.Count).IsEqualTo(1);
    await Assert.That(h.Coordinator.CommittedCount).IsEqualTo(0);

    await cts.CancelAsync();
    await h.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Composite_ChildrenCarryDeterministicIds_AcrossTwoDispatchesOfTheSameRowAsync() {
    using var h = _harness(new _composite(new _rowAdded("a"), new _rowAdded("b")));
    using var cts = new CancellationTokenSource();
    await h.Worker.StartAsync(cts.Token);

    var work = _compositeWork();
    await h.Inbox.WriteAsync(work, cts.Token);
    await h.Inbox.WriteAsync(work, cts.Token);
    var first = await h.Coordinator.Commit(1).WaitAsync(TimeSpan.FromSeconds(5));
    var second = await h.Coordinator.Commit(2).WaitAsync(TimeSpan.FromSeconds(5));

    var ids = new[] { first, second }.Select(c => c.NewInboxMessages!.Select(m => m.MessageId).OrderBy(g => g).ToList()).ToList();
    await Assert.That(ids[1].Count).IsEqualTo(2).Because("this fake coordinator never deletes the row, so the second dispatch expands too; the ids are what protect the store");
    await Assert.That(ids[0]).IsEquivalentTo(ids[1])
      .Because("a repeated expansion yields the rows the first one yielded, so the inbox primary key absorbs it");

    await cts.CancelAsync();
    await h.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Composite_WhoseCommitFails_ReleasesTheRowForRetry_AndCountsTheFailureAsync() {
    using var h = _harness(new _composite(new _rowAdded("a")), commitFailure: new InvalidOperationException("store unavailable"));
    using var cts = new CancellationTokenSource();
    await h.Worker.StartAsync(cts.Token);

    var failing = _compositeWork();
    await h.Inbox.WriteAsync(failing, cts.Token);
    await h.Inbox.Released(failing.MessageId).WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(h.Coordinator.CommittedCount).IsEqualTo(0)
      .Because("a failed commit leaves the row leased and unprocessed for the claim loop to re-offer; the in-flight entry must not block that retry");

    // The worker survives the failure: the next composite commits normally.
    var next = _compositeWork();
    await h.Inbox.WriteAsync(next, cts.Token);
    var commit = await h.Coordinator.Commit(1).WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(commit.InboxCompletion.MessageId).IsEqualTo(next.MessageId);

    var meter = h.MeterFactory.CreatedMeters.Single(m => m.Name == CompositeMetrics.METER_NAME);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.composites.commit_failures")).IsEqualTo(1);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.composites.expansions")).IsEqualTo(2)
      .Because("both dispatches expanded; only one committed");

    await cts.CancelAsync();
    await h.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Composite_Meters_CountReceivedExpansionsChildrenAndUnsubscribedDropsAsync() {
    using var h = _harness(
      new _composite(new _rowAdded("a"), new _rowRemoved("b"), new _rowAdded("c")),
      discardPolicy: new DiscardOneTypePolicy(nameof(_rowRemoved)));
    using var cts = new CancellationTokenSource();
    await h.Worker.StartAsync(cts.Token);

    await h.Inbox.WriteAsync(_compositeWork(), cts.Token);
    var commit = await h.Coordinator.Commit(1).WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(commit.NewInboxMessages!.Count).IsEqualTo(2)
      .Because("the child nobody subscribes to is dropped at expansion instead of being stored, leased, fetched and discarded (#736)");
    var meter = h.MeterFactory.CreatedMeters.Single(m => m.Name == CompositeMetrics.METER_NAME);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.composites.received")).IsEqualTo(1);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.composites.expansions")).IsEqualTo(1);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.composites.children_created")).IsEqualTo(2);
    await Assert.That(ProbeMeterReader.ReadTotal(meter, "whizbang.composites.children_unsubscribed")).IsEqualTo(1);

    await cts.CancelAsync();
    await h.Worker.StopAsync(CancellationToken.None);
  }
}
