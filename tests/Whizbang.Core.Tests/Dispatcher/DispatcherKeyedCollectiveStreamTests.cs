using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// A collective carrying an ordering key keeps its key's stream through the dispatcher (#963). The dispatcher mints a
/// stream id for every <c>[GenerateStreamId]</c> event; for a keyed collective the minted id is discarded by the
/// event itself, and every place the dispatcher records the stream has to read it back rather than keep the id it
/// minted.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
[Category("Dispatcher")]
[Category("CollectiveEvents")]
[NotInParallel] // ScopedEventTrackerAccessor.CurrentTracker is static
public sealed class DispatcherKeyedCollectiveStreamTests {
  public sealed record EmitKeyedFlipCommand(string Key);

  public sealed record KeyedFlipCollectiveEvent : CollectiveEventBase;

  public sealed class EmitKeyedFlipReceptor : IReceptor<EmitKeyedFlipCommand, KeyedFlipCollectiveEvent> {
    public ValueTask<KeyedFlipCollectiveEvent> HandleAsync(EmitKeyedFlipCommand message, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(new KeyedFlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), OrderingKey = message.Key });
  }

  [Test]
  public async Task PublishAsync_KeyedCollective_ReportsTheKeysStreamAsync() {
    var evt = new KeyedFlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), OrderingKey = "family-7" };
    var dispatcher = _createDispatcher();

    var receipt = await dispatcher.PublishAsync(evt);

    var expected = CollectiveOrdering.StreamIdFor(new TenantCollectiveScope("t-1"), "family-7");
    await Assert.That(evt.StreamId).IsEqualTo(expected);
    await Assert.That(receipt.StreamId).IsEqualTo(expected);
  }

  [Test]
  public async Task CascadedKeyedCollective_IsTrackedOnTheKeysStreamAsync() {
    var tracker = new ScopedEventTracker();
    var dispatcher = _createDispatcher();

    ScopedEventTrackerAccessor.CurrentTracker = tracker;
    try {
      await dispatcher.LocalInvokeAsync<EmitKeyedFlipCommand, KeyedFlipCollectiveEvent>(new EmitKeyedFlipCommand("family-7"));
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }

    var tracked = tracker.GetEmittedEvents().Where(e => e.EventType == typeof(KeyedFlipCollectiveEvent)).ToList();
    await Assert.That(tracked.Count).IsEqualTo(1);
    await Assert.That(tracked[0].StreamId).IsEqualTo(CollectiveOrdering.StreamIdFor(new TenantCollectiveScope("t-1"), "family-7"))
      .Because("A caller waiting on the collective waits on the stream it was tracked under; the minted id is not "
        + "the stream the event is stored on.");
  }

  /// <summary>
  /// The second keyed collective published on a key carries the first as its predecessor (#1003), so a receiver can
  /// apply them in the order they were sent; a host without the tracker stamps nothing.
  /// </summary>
  /// <param name="withTracker">Whether the host registers the predecessor tracker.</param>
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task PublishAsync_SecondKeyedCollective_CarriesTheFirstAsItsPredecessorAsync(bool withTracker) {
    var first = new KeyedFlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), OrderingKey = "family-7" };
    var second = new KeyedFlipCollectiveEvent { Scope = new TenantCollectiveScope("t-1"), OrderingKey = "family-7" };
    var serializer = new PayloadCapturingSerializer();
    var dispatcher = _createDispatcher(services => {
      services.AddSingleton<IEnvelopeSerializer>(serializer);
      services.AddScoped<IWorkCoordinatorStrategy, DiscardingStrategy>();
      if (withTracker) {
        services.AddSingleton(new CollectivePredecessorTracker());
      }
    });

    var firstReceipt = await dispatcher.PublishAsync(first);
    await dispatcher.PublishAsync(second);

    await Assert.That(serializer.Serialized.Count).IsEqualTo(2)
      .Because("both collectives went through the outbox, where the link is stamped");
    await Assert.That(first.PredecessorId).IsNull();
    if (withTracker) {
      await Assert.That(second.PredecessorId).IsEqualTo(firstReceipt.MessageId.Value);
      await Assert.That(second.PredecessorType).IsEqualTo(TypeNameFormatter.Format(typeof(KeyedFlipCollectiveEvent)));
    } else {
      await Assert.That(second.PredecessorId).IsNull();
    }
  }

  private sealed class PayloadCapturingSerializer : IEnvelopeSerializer {
    public List<object?> Serialized { get; } = [];

    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      Serialized.Add(envelope.Payload);
      var json = new MessageEnvelope<System.Text.Json.JsonElement> {
        MessageId = envelope.MessageId,
        Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
        Hops = [],
        DispatchContext = envelope.DispatchContext,
      };
      return new SerializedEnvelope(json,
        typeof(MessageEnvelope<>).MakeGenericType(typeof(TMessage)).AssemblyQualifiedName!,
        typeof(TMessage).AssemblyQualifiedName!);
    }

    public object DeserializeMessage(MessageEnvelope<System.Text.Json.JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
  }

  private sealed class DiscardingStrategy : IWorkCoordinatorStrategy {
    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }

  private static IDispatcher _createDispatcher(Action<IServiceCollection>? configure = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    configure?.Invoke(services);
    return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
  }
}
