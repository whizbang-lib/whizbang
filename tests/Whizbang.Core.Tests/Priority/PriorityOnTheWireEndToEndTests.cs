using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Priority;

/// <summary>A plain message whose name ends in Event so the publish strategy routes it to its own destination.</summary>
internal sealed record WirePriorityProbeEvent(string Value);

[JsonSerializable(typeof(WirePriorityProbeEvent))]
[JsonSerializable(typeof(MessageEnvelope<WirePriorityProbeEvent>))]
internal sealed partial class WirePriorityJsonContext : JsonSerializerContext;

/// <summary>
/// Priority step 1 end to end through the in-memory transport, composed from the real components in the order a
/// message travels: the dispatcher (with the framework's producer hooks) and the envelope serializer build the
/// outbox row; the row is stored in its JSON form and handed to the outbox drain worker; the drain publishes
/// through the real transport publish strategy over an in-process transport; the consumer worker (with the
/// framework's receive hooks) receives it and builds the inbox row; the inbox drain worker turns that row into
/// the work item the dispatch worker would run; and the payload is reconstructed into the typed envelope a handler
/// sees. Only the coordinator (the database) is replaced, by an in-memory row holder, so every stored point is a
/// real object the framework built. The number is asserted at each of them: a copy dropped at any one seam lands
/// the message in the wrong lane at the consumer, whatever the producer declared.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#on-the-wire</docs>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/EnvelopeSerializer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxDrainWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/TransportPublishStrategy.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/ServiceBusConsumerWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/ReceivedInboxMessageBuilder.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/InboxDrainWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Observability/MessageEnvelopeExtensions.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class PriorityOnTheWireEndToEndTests {
  private const string TOPIC = "wire-priority-topic";

  /// <summary>Every stored point the number passes on its way from a dispatch to a handler.</summary>
  private sealed record WireTrace(
    OutboxMessage Outbox,
    string StoredOutboxJson,
    IMessageEnvelope Wire,
    InboxMessage InboxRow,
    InboxWork Work,
    IMessageEnvelope HandlerEnvelope);

  [Test]
  public async Task Interactive_ADispatchOutsideAnyHandling_CarriesInteractiveToEveryStoredPointAsync() {
    var trace = await _runAsync(d => d.SendAsync(new WirePriorityProbeEvent("click"), MessageContext.New()));

    await _assertEverywhereAsync(trace, WorkPriority.INTERACTIVE,
      "an application-initiated dispatch has a caller waiting, and the consumer must learn that from the wire");
  }

  [Test]
  public async Task Background_ADispatchWhileHandlingBackgroundWork_CarriesBackgroundToEveryStoredPointAsync() {
    var trace = await _runAsync(async d => {
      using (PriorityContext.Enter(WorkPriority.BACKGROUND)) {
        await d.SendAsync(new WirePriorityProbeEvent("bulk"), MessageContext.New());
      }
    });

    await _assertEverywhereAsync(trace, WorkPriority.BACKGROUND,
      "a message produced while handling bulk work is bulk work at every consumer, which is the point of inheritance");
  }

  [Test]
  public async Task Explicit_ANumberOnTheDispatchOptions_CarriesThatNumberToEveryStoredPointAsync() {
    var trace = await _runAsync(d => d.SendAsync(new WirePriorityProbeEvent("seven"), new DispatchOptions().WithPriority(7)));

    await _assertEverywhereAsync(trace, 7,
      "an explicit declaration travels as the exact number, not a band; every seam copies, none rounds");
  }

  [Test]
  public async Task TheStoredOutboxEnvelope_NamesTheNumberAsPriAsync() {
    var trace = await _runAsync(async d => {
      using (PriorityContext.Enter(WorkPriority.BACKGROUND)) {
        await d.SendAsync(new WirePriorityProbeEvent("json"), MessageContext.New());
      }
    });

    await Assert.That(trace.StoredOutboxJson).Contains("\"pri\":250")
      .Because("the storage form is what the drain deserializes after a restart; a number the JSON does not carry is lost to every fetch that predates the column");
  }

  private static async Task _assertEverywhereAsync(WireTrace trace, int expected, string why) {
    await Assert.That(trace.Outbox.Priority).IsEqualTo(expected)
      .Because($"the outbox row: {why}");
    await Assert.That(trace.Outbox.Envelope.Priority).IsEqualTo(expected)
      .Because("the envelope stored with the outbox row is what the drain rebuilds from");
    await Assert.That(trace.StoredOutboxJson).Contains($"\"pri\":{expected}")
      .Because("the JSON form of the stored envelope must name the number under pri");
    await Assert.That(trace.Wire.Priority).IsEqualTo(expected)
      .Because("the envelope the transport carries is how every other service learns the number");
    await Assert.That(trace.InboxRow.Priority).IsEqualTo(expected)
      .Because("the consumer's inbox row is what its claim orders by");
    await Assert.That(trace.InboxRow.Envelope.Priority).IsEqualTo(expected)
      .Because("the envelope stored with the inbox row is what the inbox drain rebuilds from");
    await Assert.That(trace.Work.Priority).IsEqualTo(expected)
      .Because("the work item is what the dispatch worker enters the handling with");
    await Assert.That(trace.Work.Envelope.Priority).IsEqualTo(expected)
      .Because("the work item's envelope is what the typed reconstruction copies from");
    await Assert.That(trace.HandlerEnvelope.Priority).IsEqualTo(expected)
      .Because("the typed envelope the handler sees is what inheritance reads for everything the handler emits");
  }

  private static async Task<WireTrace> _runAsync(Func<OutboxOnlyDispatcher, Task> dispatch) {
    JsonContextRegistry.RegisterContext(WirePriorityJsonContext.Default);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var serializer = new EnvelopeSerializer(options);
    var envelopeTypeInfo = options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("Test setup: no JsonTypeInfo for MessageEnvelope<JsonElement>.");
    using var cts = new CancellationTokenSource();

    // 1. Producer: the real dispatcher with the framework's producer hooks and the real envelope serializer.
    var producerStrategy = new ProducerStrategy();
    var producerServices = new ServiceCollection();
    producerServices.AddSingleton<IServiceScopeFactory>(sp => new TestScopeFactory(sp));
    producerServices.AddSingleton<IWorkCoordinatorStrategy>(producerStrategy);
    producerServices.AddWhizbangPriority();
    await using var producerProvider = producerServices.BuildServiceProvider();
    await dispatch(new OutboxOnlyDispatcher(producerProvider, serializer));
    var outbox = producerStrategy.QueuedOutbox.Single();

    // 2. The outbox row as the store keeps it: the envelope in its JSON form, the number on the row.
    var storedOutboxJson = JsonSerializer.Serialize((MessageEnvelope<JsonElement>)outbox.Envelope, envelopeTypeInfo);
    var streamId = outbox.StreamId ?? outbox.MessageId;
    var outboxRow = new OutboxBatchRow {
      MessageId = outbox.MessageId,
      StreamId = streamId,
      Destination = TOPIC,
      MessageType = outbox.MessageType,
      EnvelopeType = outbox.EnvelopeType,
      EventData = storedOutboxJson,
      Metadata = "{}",
      Scope = null,
      Status = 1,
      Attempts = 0,
      PartitionNumber = 0,
      IsEvent = false,
      Priority = outbox.Priority,
    };

    // 3. Consumer: the real consumer worker with the framework's receive hooks, subscribed on the in-process transport.
    var transport = new InProcessTransport();
    var wireCaptured = new TaskCompletionSource<IMessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
    await transport.SubscribeBatchAsync(
      (batch, _) => {
        wireCaptured.TrySetResult(batch[0].Envelope);
        return Task.CompletedTask;
      },
      new TransportDestination(TOPIC),
      new TransportBatchOptions { BatchSize = 1, SlideMs = 10, MaxWaitMs = 100 });
    var consumerStrategy = new ConsumerStrategy();
    var consumerServices = new ServiceCollection();
    consumerServices.AddSingleton<IServiceInstanceProvider>(new InstanceProvider());
    consumerServices.AddScoped<IWorkCoordinatorStrategy>(_ => consumerStrategy);
    consumerServices.AddWhizbangPriority();
    await using var consumerProvider = consumerServices.BuildServiceProvider();
    var consumer = new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: consumerProvider.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: options,
      logger: NullLogger<ServiceBusConsumerWorker>.Instance,
      orderedProcessor: new OrderedStreamProcessor(),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      options: new ServiceBusConsumerOptions { Subscriptions = [new TopicSubscription(TOPIC, "wire-priority-sub")] },
      envelopeSerializer: serializer,
      receptorRegistry: new SubscribedRegistry());
    await consumer.StartAsync(cts.Token);
    await consumer.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    // 4. Drain: the real outbox drain worker publishing through the real transport publish strategy.
    var coordinator = new PipelineCoordinator { LocalServiceId = (Guid)TrackedGuid.NewMedo() };
    coordinator.OutboxRowsByStream[streamId] = [outboxRow];
    var workerServices = new ServiceCollection();
    workerServices.AddSingleton<IWorkCoordinator>(coordinator);
    await using var workerProvider = workerServices.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var drainChannel = new DrainChannel();
    var completion = new CompletionChannel();
    var drain = new OutboxDrainWorker(
      workerProvider.GetRequiredService<IServiceScopeFactory>(),
      new InstanceProvider(),
      drainChannel,
      completion,
      new FailureChannel(),
      gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true }),
      options,
      NullLogger<OutboxDrainWorker>.Instance,
      new TransportPublishStrategy(transport, new DefaultTransportReadinessCheck(), "wire-priority-inbox"));
    await drain.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);
    var wire = await wireCaptured.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var inboxRow = await consumerStrategy.Stored.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await completion.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

    // 5. Inbox drain: the consumer's row, as its store keeps it, turned into the work item the dispatch worker runs.
    var inboxStreamId = inboxRow.StreamId ?? inboxRow.MessageId;
    coordinator.InboxRowsByStream[inboxStreamId] = [
      new InboxBatchRow {
        MessageId = inboxRow.MessageId,
        StreamId = inboxStreamId,
        HandlerName = inboxRow.HandlerName,
        MessageType = inboxRow.MessageType,
        EventData = JsonSerializer.Serialize((MessageEnvelope<JsonElement>)inboxRow.Envelope, envelopeTypeInfo),
        Metadata = "{}",
        Scope = null,
        Status = 1,
        Attempts = 0,
        PartitionNumber = 0,
        IsEvent = false,
        Priority = inboxRow.Priority,
      }
    ];
    var inboxDrainChannel = new InboxDrainChannel();
    var inboxChannel = new InboxChannel();
    var inboxDrain = new InboxDrainWorker(
      workerProvider.GetRequiredService<IServiceScopeFactory>(),
      new InstanceProvider(),
      inboxDrainChannel,
      inboxChannel,
      gate,
      Options.Create(new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      options,
      NullLogger<InboxDrainWorker>.Instance);
    await inboxDrain.StartAsync(cts.Token);
    await inboxDrainChannel.WriteAsync(inboxStreamId);
    var work = await inboxChannel.First.Task.WaitAsync(TimeSpan.FromSeconds(10));

    // 6. The handler's view: the payload deserialized and the typed envelope reconstructed around it.
    var payload = JsonSerializer.Deserialize(work.Envelope.Payload.GetRawText(), WirePriorityJsonContext.Default.WirePriorityProbeEvent)
      ?? throw new InvalidOperationException("Test setup: the payload did not deserialize.");
    var handlerEnvelope = work.Envelope.ReconstructWithPayload(payload, handlerName: work.HandlerName);

    await cts.CancelAsync();
    foreach (var hosted in new Microsoft.Extensions.Hosting.IHostedService[] { inboxDrain, drain, consumer }) {
      try { await hosted.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
    }

    return new WireTrace(outbox, storedOutboxJson, wire, inboxRow, work, handlerEnvelope);
  }

  #region Producer side

  private sealed class OutboxOnlyDispatcher(IServiceProvider sp, IEnvelopeSerializer serializer) : Core.Dispatcher(
      sp, new ServiceInstanceProvider(configuration: null), envelopeSerializer: serializer) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) => _ => Task.CompletedTask;
    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => null;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => null;
  }

  private sealed class TestScopeFactory(IServiceProvider provider) : IServiceScopeFactory {
    public IServiceScope CreateScope() => new TestScope(provider);
    private sealed class TestScope(IServiceProvider provider) : IServiceScope {
      public IServiceProvider ServiceProvider { get; } = provider;
      public void Dispose() { }
    }
  }

  private sealed class ProducerStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> QueuedOutbox { get; } = [];
    public void QueueOutboxMessage(OutboxMessage message) => QueuedOutbox.Add(message);
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
  }

  #endregion

  #region Consumer side

  private sealed class ConsumerStrategy : IWorkCoordinatorStrategy {
    public TaskCompletionSource<InboxMessage> Stored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) => Stored.TrySetResult(message);
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
  }

  private sealed class SubscribedRegistry : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => true;
    public bool HasAnyConsumer(string messageType) => true;
  }

  #endregion

  #region Workers' surroundings

  private sealed class InstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "wire-priority-svc";
    public string HostName => "wire-priority-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  /// <summary>The coordinator stands in for the database: it holds the rows each drain fetches.</summary>
  private sealed class PipelineCoordinator : IWorkCoordinator {
    public Dictionary<Guid, List<OutboxBatchRow>> OutboxRowsByStream { get; } = [];
    public Dictionary<Guid, List<InboxBatchRow>> InboxRowsByStream { get; } = [];
    public Guid LocalServiceId { get; set; }
    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(LocalServiceId);
    public Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
        IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) {
      var result = new List<OutboxBatchRow>();
      lock (OutboxRowsByStream) {
        foreach (var sid in streamIds) {
          if (OutboxRowsByStream.TryGetValue(sid, out var rows)) {
            var taken = rows.Take(maxPerStream).ToList();
            result.AddRange(taken);
            rows.RemoveRange(0, taken.Count);
          }
        }
      }
      return Task.FromResult<IReadOnlyList<OutboxBatchRow>>(result);
    }
    public Task<IReadOnlyList<InboxBatchRow>> FetchInboxBatchAsync(
        IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) {
      var result = new List<InboxBatchRow>();
      lock (InboxRowsByStream) {
        foreach (var sid in streamIds) {
          if (InboxRowsByStream.TryGetValue(sid, out var rows)) {
            var taken = rows.Take(maxPerStream).ToList();
            result.AddRange(taken);
            rows.RemoveRange(0, taken.Count);
          }
        }
      }
      return Task.FromResult<IReadOnlyList<InboxBatchRow>>(result);
    }
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string name, CancellationToken ct = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class DrainChannel : IOutboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) => _channel.Writer.WriteAsync(streamId, cancellationToken);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
  }

  private sealed class CompletionChannel : IOutboxCompletionChannel {
    public ConcurrentBag<Guid> AllIds { get; } = [];
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) {
      AllIds.Add(id);
      Reached.TrySetResult();
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FailureChannel : IFailureChannel {
    public ConcurrentBag<MessageFailure> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add(failure);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class InboxDrainChannel : IInboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) => _channel.Writer.WriteAsync(streamId, ct);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
    public void Complete() => _channel.Writer.Complete();
  }

  private sealed class InboxChannel : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public TaskCompletionSource<InboxWork> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) {
      First.TrySetResult(work);
      return _channel.Writer.WriteAsync(work, ct);
    }
    public bool TryWrite(InboxWork work) {
      First.TrySetResult(work);
      return _channel.Writer.TryWrite(work);
    }
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  #endregion
}
