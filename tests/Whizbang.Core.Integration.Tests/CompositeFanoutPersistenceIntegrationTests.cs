using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Integration.Tests.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Integration.Tests;

/// <summary>
/// End-to-end coverage for composite fan-out, publishing an OWNED composite through the REAL source-generated
/// dispatcher (<c>AddReceptors()</c> + <c>AddWhizbangDispatcher()</c>) and observing what reaches the
/// outbox/event-store seam (a spy <see cref="IWorkCoordinatorStrategy"/>). A Destination==null row is an
/// event-store-only write (inner-event persistence); a non-null Destination is a transported row (the composite
/// itself, for other subscribers to fan out on receive). Two behaviours are locked here:
/// <list type="number">
///   <item><description><b>Name resolution (the generator fix):</b> the composite is published to the outbox and
///   its type is resolvable by name via <see cref="JsonContextRegistry.GetTypeInfoByName"/> — the lookup the
///   outbox-flush and inbox fan-out lifecycle use per row. Before the fix a composite got
///   <c>RegisterDerivedType</c> (polymorphism) only, never <c>RegisterTypeName</c>, so that returned null → the
///   "Failed to resolve message type" storm.</description></item>
///   <item><description><b>Inner-event persistence:</b> an owned composite fans out EVERY inner event to the event
///   store at publish — one whose type a receptor produces (concrete cascade arm) AND one that no receptor
///   produces (the IEvent catch-all fallback), so a composite never silently loses a child.</description></item>
/// </list>
/// </summary>
[Category("Integration")]
public class CompositeFanoutPersistenceIntegrationTests {

  // A command + receptor whose RESPONSE type is E2eInnerProduced. Its existence puts E2eInnerProduced in the
  // generator's event-type set, so the generated dispatcher emits an event-store cascade arm for it.
  public sealed record E2eProducerCommand(string Note);

  public sealed record E2eInnerProduced : IEvent {
    [StreamId] public Guid StreamId { get; init; }
    public string? Note { get; init; }
  }

  public sealed class E2eProducerReceptor : IReceptor<E2eProducerCommand, E2eInnerProduced> {
    public ValueTask<E2eInnerProduced> HandleAsync(E2eProducerCommand message, CancellationToken cancellationToken = default)
      => ValueTask.FromResult(new E2eInnerProduced { Note = message.Note });
  }

  // An inner event type NO receptor returns — so the generator emits no event-store cascade arm for it.
  public sealed record E2eInnerOrphan : IEvent {
    [StreamId] public Guid StreamId { get; init; }
    public string? Note { get; init; }
  }

  public sealed class E2eOwnedComposite : CompositeEventBase;

  // Spy strategy: captures every outbox row the dispatcher queues. Destination==null rows are event-store-only
  // (inner-event persistence); a non-null Destination is a transported row (the composite for subscribers).
  private sealed class SpyWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> QueuedOutboxMessages { get; } = [];

    public void QueueOutboxMessage(OutboxMessage message) => QueuedOutboxMessages.Add(message);
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => FlushAndGetBatchAsync(flags, ct);
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default)
      => Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }

  private sealed class StubEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonEnvelope = new MessageEnvelope<System.Text.Json.JsonElement> {
        MessageId = envelope.MessageId,
        Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { }),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      };
      return new SerializedEnvelope(
        jsonEnvelope,
        typeof(MessageEnvelope<>).MakeGenericType(typeof(TMessage)).AssemblyQualifiedName!,
        typeof(TMessage).AssemblyQualifiedName!);
    }

    public object DeserializeMessage(MessageEnvelope<System.Text.Json.JsonElement> jsonEnvelope, string messageTypeName)
      => throw new NotSupportedException();
  }

  private static (IDispatcher dispatcher, SpyWorkCoordinatorStrategy strategy) _createOwnedCompositeDispatcher() {
    var strategy = new SpyWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddSingleton<ITopicRoutingStrategy>(new NamespaceRoutingStrategy());
    // Own the composite's namespace so it fans out LOCALLY at publish (owned-domain composites materialize their
    // own children immediately; a non-owned composite would fan out only at the receive side).
    services.Configure<RoutingOptions>(o => o.OwnDomains(typeof(E2eOwnedComposite).Namespace!));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    return (provider.GetRequiredService<IDispatcher>(), strategy);
  }

  private static E2eOwnedComposite _newComposite(Guid streamId) => new() {
    StreamId = streamId,
    Inner = [
      new E2eInnerProduced { StreamId = streamId, Note = "produced" },
      new E2eInnerOrphan { StreamId = streamId, Note = "orphan" },
    ],
  };

  [Test]
  public async Task OwnedComposite_PublishedThroughRealDispatcher_IsNameResolvable_And_PersistsProducedInnerEventAsync() {
    var (dispatcher, strategy) = _createOwnedCompositeDispatcher();
    var streamId = (Guid)TrackedGuid.NewMedo();

    await dispatcher.PublishAsync(_newComposite(streamId));

    // The composite itself is published to the outbox for other subscribers (a transported row, tagged Composite).
    var compositeRows = strategy.QueuedOutboxMessages
      .Where(m => m.EnvelopeType.Contains("E2eOwnedComposite", StringComparison.Ordinal)).ToList();
    await Assert.That(compositeRows.Count).IsEqualTo(1)
      .Because("an owned composite still goes over transport (step 1.2) so other subscribers can fan it out.");
    await Assert.That(compositeRows[0].Flags.HasFlag(EventFlags.Composite)).IsTrue()
      .Because("the composite outbox row is tagged Composite so the receiver knows to expand it.");
    await Assert.That(compositeRows[0].Destination).IsNotNull()
      .Because("the composite is transported (non-null destination), unlike an owned regular event which is event-store-only.");

    // The generator fix: the composite must be resolvable by name — the outbox-flush / inbox fan-out lifecycle
    // deserialize each queued row via GetTypeInfoByName. The crux is composite==event PARITY: before the fix a
    // regular event resolved but a composite returned null → the "Failed to resolve message type" storm.
    var options = JsonContextRegistry.CreateCombinedOptions();
    await Assert.That(JsonContextRegistry.GetTypeInfoByName(typeof(E2eInnerProduced).AssemblyQualifiedName!, options)).IsNotNull()
      .Because("control: a regular event is name-resolvable via RegisterTypeName.");
    await Assert.That(JsonContextRegistry.GetTypeInfoByName(typeof(E2eOwnedComposite).AssemblyQualifiedName!, options)).IsNotNull()
      .Because("the fix registers composites (RegisterTypeName), so the outbox-flush / inbox lifecycle resolves the composite by name instead of throwing 'Failed to resolve message type'.");

    // The produced inner event fans out to the event-store seam (Destination == null = persist, no transport).
    var producedRows = strategy.QueuedOutboxMessages
      .Where(m => m.Destination is null && m.EnvelopeType.Contains("E2eInnerProduced", StringComparison.Ordinal)).ToList();
    await Assert.That(producedRows.Count).IsEqualTo(1)
      .Because("an inner event whose type a receptor produces has a generated event-store cascade arm and persists at publish.");
    await Assert.That(producedRows[0].IsEvent).IsTrue()
      .Because("the persisted inner event row is flagged IsEvent so the event store writes it under the composite's stream.");
  }

  [Test]
  public async Task OwnedComposite_ReceptorlessInnerEvent_PersistsViaCascadeFallbackAsync() {
    // Persistence-completeness fix: an inner event type that NO receptor produces (so the generated
    // CascadeToEventStoreOnlyAsync type-switch has no concrete arm for it — the arm set is built from receptor
    // response types) must STILL persist to the event store. The generated IEvent catch-all fallback routes it
    // via runtime-typed dispatch (PublishToOutboxDynamicAsync). Without this, the override silently returned
    // without persisting — no exception, no Warning — so a composite whose stream-creating inner event is
    // not receptor-produced never materializes its read-model instance.
    var (dispatcher, strategy) = _createOwnedCompositeDispatcher();
    var streamId = (Guid)TrackedGuid.NewMedo();

    await dispatcher.PublishAsync(_newComposite(streamId));

    var orphanRows = strategy.QueuedOutboxMessages
      .Where(m => m.Destination is null && m.EnvelopeType.Contains("E2eInnerOrphan", StringComparison.Ordinal)).ToList();
    await Assert.That(orphanRows.Count).IsEqualTo(1)
      .Because("the cascade IEvent fallback persists EVERY inner event of an owned composite, even one whose type no standalone receptor produces.");
    await Assert.That(orphanRows[0].IsEvent).IsTrue()
      .Because("the persisted receptor-less inner event is flagged IsEvent so the event store writes it under the composite's stream.");
  }
}
