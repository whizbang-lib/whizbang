using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Two properties of composite fan-out measured wrong under a bulk import (#736, #737): children got fresh
/// ids on every expansion, so a repeated expansion could not be recognized; and a child whose type the
/// consumer's catalog did not list was stored as a command (<c>is_event = false</c>) and served ahead of
/// real commands, then discarded at dispatch. Children now carry deterministic ids, a child a consumer has
/// no subscription for is dropped at expansion and counted, and classification is positive: a composite
/// child is an event unless something says otherwise.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#deterministic-child-ids</docs>
[Category("Unit")]
public class CompositeInboxFanoutIdentityAndSubscriptionTests {

  private sealed record _rowAdded(string Id) : IEvent;
  private sealed record _rowRemoved(string Id) : IEvent;

  private sealed class _composite(params IMessage[] inner) : ICompositeEvent {
    public IEnumerable<IMessage> InnerEvents => inner;
  }

  /// <summary>A raw bundle that carries no child ids: the shape a producer's transport packaging has.</summary>
  private sealed class _rawBundle(Guid streamId, params (string Type, string Json)[] inner) : IRawInnerComposite {
    public IEnumerable<IMessage> InnerEvents => [];
    public IReadOnlyList<JsonElement> InnerPayloads { get; } = inner.Select(i => JsonSerializer.Deserialize<JsonElement>(i.Json)).ToList();
    public IReadOnlyList<string> InnerTypeNames { get; } = inner.Select(i => i.Type).ToList();
    public Guid StreamId => streamId;
  }

  private sealed class _emptyCatalog : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class _serializer : IEnvelopeSerializer {
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

  private static ServiceProvider _scope(bool withEmptyCatalog = false) {
    var services = new ServiceCollection().AddSingleton<IEnvelopeSerializer>(new _serializer());
    if (withEmptyCatalog) {
      services.AddSingleton<IEventTypeProvider>(new _emptyCatalog());
    }
    return services.BuildServiceProvider();
  }

  private static MessageEnvelope<JsonElement> _source(Guid streamId, Guid? messageId = null) => new() {
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    MessageId = messageId is { } id ? MessageId.From(id) : MessageId.New(),
    Payload = JsonSerializer.SerializeToElement(new { }),
    Hops = [new MessageHop {
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Metadata = new Dictionary<string, JsonElement> { ["AggregateId"] = JsonSerializer.SerializeToElement(streamId.ToString()) },
    }],
    SourceServiceId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
    SourceCommitSequence = 42,
  };

  // ---- deterministic ids ------------------------------------------------------------------------

  [Test]
  public async Task TryExpand_TypedComposite_ExpandedTwice_YieldsTheSameChildIdsAsync() {
    var streamId = Guid.CreateVersion7();
    var compositeId = Guid.CreateVersion7();
    var composite = new _composite(new _rowAdded("a"), new _rowAdded("b"), new _rowRemoved("c"));

    var first = CompositeInboxFanout.TryExpand(composite, _source(streamId, compositeId), _scope());
    var second = CompositeInboxFanout.TryExpand(composite, _source(streamId, compositeId), _scope());

    await Assert.That(first.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(first.Children.Select(c => c.MessageId).ToList()).IsEquivalentTo(second.Children.Select(c => c.MessageId).ToList())
      .Because("a re-offered composite expanded again must produce the rows the first expansion produced, so the inbox primary key drops the repeat instead of storing a second copy");
    await Assert.That(first.Children.Select(c => c.MessageId).Distinct().Count()).IsEqualTo(3)
      .Because("the children of one expansion are still distinct rows");
    await Assert.That(first.Children[0].Envelope.MessageId.Value).IsEqualTo(first.Children[0].MessageId)
      .Because("the envelope inside the row carries the same derived id as the row");
  }

  [Test]
  public async Task TryExpand_TypedComposite_ChildIdsFollowTheCompositeIdAsync() {
    var streamId = Guid.CreateVersion7();
    var composite = new _composite(new _rowAdded("a"));

    var one = CompositeInboxFanout.TryExpand(composite, _source(streamId, Guid.CreateVersion7()), _scope());
    var other = CompositeInboxFanout.TryExpand(composite, _source(streamId, Guid.CreateVersion7()), _scope());

    await Assert.That(one.Children[0].MessageId).IsNotEqualTo(other.Children[0].MessageId)
      .Because("the same inner event carried by two different composites is two deliveries, not a repeat");
  }

  [Test]
  public async Task TryExpand_RawBundleWithoutChildIds_ExpandedTwice_YieldsTheSameChildIdsAsync() {
    var streamId = Guid.CreateVersion7();
    var compositeId = Guid.CreateVersion7();
    var bundle = new _rawBundle(streamId, ("Contracts.Job.RowAddedEvent, Contracts", "{\"v\":1}"), ("Contracts.Job.RowAddedEvent, Contracts", "{\"v\":2}"));

    var first = CompositeInboxFanout.TryExpand(bundle, _source(streamId, compositeId), _scope());
    var second = CompositeInboxFanout.TryExpand(bundle, _source(streamId, compositeId), _scope());

    await Assert.That(first.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(first.Children.Select(c => c.MessageId).ToList()).IsEquivalentTo(second.Children.Select(c => c.MessageId).ToList())
      .Because("a raw bundle without caller-supplied ids derives them the same way the typed path does");
    await Assert.That(first.Children[0].MessageId).IsNotEqualTo(first.Children[1].MessageId);
  }

  [Test]
  public async Task TryExpand_IdentityPreservingComposite_KeepsTheCallersIdsAsync() {
    var streamId = Guid.CreateVersion7();
    var original = Guid.CreateVersion7();
    var composite = new RedeliveryComposite {
      StreamId = streamId,
      InnerPayloads = [JsonSerializer.Deserialize<JsonElement>("{\"v\":1}")],
      InnerTypeNames = ["Contracts.Job.RowAddedEvent, Contracts"],
      InnerEventIds = [original],
    };

    var result = CompositeInboxFanout.TryExpand(composite, _source(streamId), _scope());

    await Assert.That(result.Children[0].MessageId).IsEqualTo(original)
      .Because("a re-delivery bundle carries previously persisted events whose ids consumers converge on; derivation never overrides a supplied id");
  }

  // ---- positive classification -------------------------------------------------------------------

  [Test]
  public async Task TryExpand_TypedChildMissingFromTheCatalog_IsStillAnEvent_WhenItIsOneAsync() {
    var composite = new _composite(new _rowAdded("a"));

    var result = CompositeInboxFanout.TryExpand(composite, _source(Guid.CreateVersion7()), _scope(withEmptyCatalog: true));

    await Assert.That(result.Children[0].IsEvent).IsTrue()
      .Because("a catalog miss must not demote an event to a command; the IEvent marker is authoritative (#736)");
  }

  [Test]
  public async Task TryExpand_RawChildMissingFromTheCatalog_IsAnEventAsync() {
    var streamId = Guid.CreateVersion7();
    var bundle = new _rawBundle(streamId, ("Contracts.Job.RowAddedEvent, Contracts", "{\"v\":1}"));

    var result = CompositeInboxFanout.TryExpand(bundle, _source(streamId), _scope(withEmptyCatalog: true));

    await Assert.That(result.Children[0].IsEvent).IsTrue()
      .Because("raw children come from an origin's event store; a type the consumer never registered is still an event and never enters the command lane (#736)");
  }

  // ---- unsubscribed children are dropped at expansion --------------------------------------------

  [Test]
  public async Task TryExpand_WithAConsumerPredicate_DropsChildrenNobodySubscribesTo_AndCountsThemAsync() {
    var composite = new _composite(new _rowAdded("a"), new _rowRemoved("b"), new _rowAdded("c"));

    var result = CompositeInboxFanout.TryExpand(
      composite, _source(Guid.CreateVersion7()), _scope(),
      hasConsumer: typeName => typeName.Contains(nameof(_rowAdded), StringComparison.Ordinal));

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children.Count).IsEqualTo(2)
      .Because("a child the consumer has no handler for would be stored, leased, fetched and then discarded; dropping it here costs nothing");
    await Assert.That(result.Children.All(c => c.MessageType.Contains(nameof(_rowAdded), StringComparison.Ordinal))).IsTrue();
    await Assert.That(result.UnsubscribedChildren).IsEqualTo(1)
      .Because("the drop is counted so the meters can show how much of a composite a consumer actually wanted");
  }

  [Test]
  public async Task TryExpand_WithAConsumerPredicate_RawBundle_DropsUnsubscribedChildrenAsync() {
    var streamId = Guid.CreateVersion7();
    var bundle = new _rawBundle(streamId, ("Contracts.Job.RowAddedEvent, Contracts", "{\"v\":1}"), ("Contracts.Job.RowRemovedEvent, Contracts", "{\"v\":2}"));

    var result = CompositeInboxFanout.TryExpand(bundle, _source(streamId), _scope(), hasConsumer: t => t.StartsWith("Contracts.Job.RowAdded", StringComparison.Ordinal));

    await Assert.That(result.Children.Count).IsEqualTo(1);
    await Assert.That(result.UnsubscribedChildren).IsEqualTo(1);
  }

  [Test]
  public async Task TryExpand_WithoutAPredicate_KeepsEveryChildAsync() {
    var composite = new _composite(new _rowAdded("a"), new _rowRemoved("b"));

    var result = CompositeInboxFanout.TryExpand(composite, _source(Guid.CreateVersion7()), _scope());

    await Assert.That(result.Children.Count).IsEqualTo(2);
    await Assert.That(result.UnsubscribedChildren).IsEqualTo(0)
      .Because("with no subscription knowledge the fan-out keeps the behavior it had; only a positive answer drops a child");
  }

  [Test]
  public async Task TryExpand_DroppedChildren_DoNotConsumeTheOrdinalsOfTheKeptOnesAsync() {
    // The ids must not depend on what a given consumer subscribes to, or two consumers of one
    // composite would derive different ids for the same child and the audit trail across services
    // could not match them up.
    var streamId = Guid.CreateVersion7();
    var compositeId = Guid.CreateVersion7();
    var composite = new _composite(new _rowRemoved("x"), new _rowAdded("a"));

    var filtered = CompositeInboxFanout.TryExpand(composite, _source(streamId, compositeId), _scope(), hasConsumer: t => t.Contains(nameof(_rowAdded), StringComparison.Ordinal));
    var full = CompositeInboxFanout.TryExpand(composite, _source(streamId, compositeId), _scope());

    await Assert.That(filtered.Children[0].MessageId).IsEqualTo(full.Children[1].MessageId)
      .Because("the kept child keeps the id it has when nothing is dropped: ordinals are positions in the composite, not in the kept set");
  }
}
