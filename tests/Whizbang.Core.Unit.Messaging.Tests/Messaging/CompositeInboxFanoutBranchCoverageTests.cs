// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// Branch coverage for <see cref="CompositeInboxFanout"/>: a source envelope whose hop list is null
/// (lineage hop and stream fallback), classification of a non-event child through the event-type
/// catalog (absent, unavailable, listing it, not listing it), and the raw path's origin override.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/CompositeInboxFanout.cs</code-under-test>
[Category("Messaging")]
public class CompositeInboxFanoutBranchCoverageTests {
  private static readonly Guid _sourceServiceId = Guid.Parse("00000000-0000-0000-0000-000000000001");

  private sealed record ChildEvent(string Id) : IEvent;
  private sealed record ChildCommand(string Id) : IMessage;

  private sealed class Composite(params IMessage[] inner) : ICompositeEvent {
    public IEnumerable<IMessage> InnerEvents => inner;
  }

  /// <summary>A raw bundle with no identity preservation: children inherit the source's service id.</summary>
  private sealed class RawBundle(string type, string json) : IRawInnerComposite {
    public IEnumerable<IMessage> InnerEvents => [];
    public IReadOnlyList<JsonElement> InnerPayloads { get; } = [JsonSerializer.Deserialize<JsonElement>(json)];
    public IReadOnlyList<string> InnerTypeNames { get; } = [type];
  }

  /// <summary>A raw, identity-preserving bundle that names the origin service its children came from.</summary>
  private sealed class OriginRawBundle(Guid originServiceId, Guid childId, string type, string json)
      : IRawInnerComposite, IIdentityPreservingComposite {
    public IEnumerable<IMessage> InnerEvents => [];
    public IReadOnlyList<JsonElement> InnerPayloads { get; } = [JsonSerializer.Deserialize<JsonElement>(json)];
    public IReadOnlyList<string> InnerTypeNames { get; } = [type];
    public IReadOnlyList<Guid> InnerEventIds { get; } = [childId];
    public Guid OriginServiceId => originServiceId;
  }

  /// <summary>
  /// A TYPED identity-preserving composite (not a raw bundle) that names the origin service its child
  /// came from, so expansion takes the typed child builder rather than the raw path.
  /// </summary>
  private sealed class OriginTypedComposite(Guid originServiceId, Guid childId, long childSequence, IMessage inner)
      : IIdentityPreservingComposite {
    public IEnumerable<IMessage> InnerEvents => [inner];
    public IReadOnlyList<Guid> InnerEventIds { get; } = [childId];
    public Guid OriginServiceId => originServiceId;
    public IReadOnlyList<long?>? InnerCommitSequences { get; } = [childSequence];
  }

  private sealed class ListingCatalog(params Type[] types) : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => types;
  }

  private sealed class Serializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var aqn = envelope.Payload!.GetType().AssemblyQualifiedName!;
      var jsonEnv = new MessageEnvelope<JsonElement> {
        DispatchContext = envelope.DispatchContext,
        MessageId = envelope.MessageId,
        Payload = JsonSerializer.SerializeToElement(new { }),
        Hops = envelope.Hops?.ToList() ?? [],
      };
      return new SerializedEnvelope(jsonEnv, $"Whizbang.Core.Observability.MessageEnvelope`1[[{aqn}]], Whizbang.Core", aqn);
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
  }

  private static ServiceProvider _scope(IEventTypeProvider? catalog = null) {
    var services = new ServiceCollection().AddSingleton<IEnvelopeSerializer>(new Serializer());
    if (catalog is not null) {
      services.AddSingleton(catalog);
    }
    return services.BuildServiceProvider();
  }

  private static MessageEnvelope<JsonElement> _source(Guid streamId) => new() {
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    MessageId = MessageId.New(),
    Payload = JsonSerializer.SerializeToElement(new { }),
    Hops = [new MessageHop {
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Metadata = new Dictionary<string, JsonElement> { ["AggregateId"] = JsonSerializer.SerializeToElement(streamId.ToString()) },
    }],
    SourceServiceId = _sourceServiceId,
    SourceCommitSequence = 42,
  };

  // ---- source with a null hop list --------------------------------------------------------------

  [Test]
  public async Task TryExpand_SourceHopListIsNull_ChildGetsOnlyTheLineageHopAndFallsBackToTheSourceIdAsStreamAsync() {
    var source = new MessageEnvelope<JsonElement> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { }),
      Hops = null!,
      SourceServiceId = _sourceServiceId,
    };
    await using var scope = _scope();

    var result = CompositeInboxFanout.TryExpand(new Composite(new ChildEvent("a")), source, scope);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    var child = result.Children.Single();
    var hops = child.Metadata!.Hops;
    await Assert.That(hops.Count).IsEqualTo(1)
      .Because("with no source journey to append, the child's chain is the lineage hop alone");
    await Assert.That(hops[0].ServiceInstance).IsEqualTo(ServiceInstanceInfo.Unknown)
      .Because("no first source hop exists to borrow a service instance from");
    await Assert.That(hops[0].CausationId).IsEqualTo(source.MessageId)
      .Because("the lineage hop still traces the child back to the composite");
    await Assert.That(child.StreamId).IsEqualTo(source.MessageId.Value)
      .Because("with no hop carrying an AggregateId, the stream falls back to the composite's own id");
  }

  [Test]
  public async Task TryExpand_NoSerializer_ThrowsAsync() {
    var source = new MessageEnvelope<JsonElement> {
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { }),
      Hops = [],
    };
    await using var scope = new ServiceCollection().BuildServiceProvider();

    await Assert.That(() => CompositeInboxFanout.TryExpand(new Composite(new ChildEvent("a")), source, scope))
      .ThrowsExactly<InvalidOperationException>().WithMessageContaining("IEnvelopeSerializer is required");
  }

  // ---- classification of a non-event child ------------------------------------------------------

  [Test]
  public async Task TryExpand_CommandChildWithoutCatalog_IsNotAnEventAsync() {
    await using var scope = _scope();

    var result = CompositeInboxFanout.TryExpand(new Composite(new ChildCommand("a")), _source(Guid.CreateVersion7()), scope);

    await Assert.That(result.Children.Single().IsEvent).IsFalse()
      .Because("without the IEvent marker and without a catalog, nothing classifies the child as an event");
  }

  [Test]
  public async Task TryExpand_CommandChildWithUnavailableCatalog_IsNotAnEventAsync() {
    await using var scope = _scope(NullEventTypeProvider.Instance);

    var result = CompositeInboxFanout.TryExpand(new Composite(new ChildCommand("a")), _source(Guid.CreateVersion7()), scope);

    await Assert.That(result.Children.Single().IsEvent).IsFalse()
      .Because("an unavailable catalog is not consulted, so it cannot promote the child");
  }

  [Test]
  public async Task TryExpand_CommandChildListedByAvailableCatalog_IsPromotedToAnEventAsync() {
    await using var scope = _scope(new ListingCatalog(typeof(ChildCommand)));

    var result = CompositeInboxFanout.TryExpand(new Composite(new ChildCommand("a")), _source(Guid.CreateVersion7()), scope);

    await Assert.That(result.Children.Single().IsEvent).IsTrue()
      .Because("the catalog can add to the IEvent marker: a type it lists as an event is stored as one");
  }

  [Test]
  public async Task TryExpand_CommandChildNotListedByAvailableCatalog_IsNotAnEventAsync() {
    await using var scope = _scope(new ListingCatalog(typeof(ChildEvent)));

    var result = CompositeInboxFanout.TryExpand(new Composite(new ChildCommand("a")), _source(Guid.CreateVersion7()), scope);

    await Assert.That(result.Children.Single().IsEvent).IsFalse()
      .Because("an available catalog that does not list the child's type leaves it a command");
  }

  // ---- raw path: origin override ----------------------------------------------------------------

  [Test]
  public async Task TryExpand_RawIdentityBundleNamingAnOrigin_ChildCarriesTheOriginServiceIdAsync() {
    var origin = Guid.CreateVersion7();
    var childId = Guid.CreateVersion7();
    await using var scope = _scope();
    var bundle = new OriginRawBundle(origin, childId, "Contracts.Job.RowAddedEvent, Contracts", "{\"v\":1}");

    var result = CompositeInboxFanout.TryExpand(bundle, _source(Guid.CreateVersion7()), scope);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    var child = result.Children.Single();
    await Assert.That(child.SourceServiceId).IsEqualTo(origin)
      .Because("a re-delivered child is recounted under the origin that emitted it, not the relaying source");
    await Assert.That(child.Envelope.SourceServiceId).IsEqualTo(origin);
    await Assert.That(child.MessageId).IsEqualTo(childId);
  }

  [Test]
  public async Task TryExpand_RawBundleWithoutOrigin_ChildInheritsTheSourceServiceIdAsync() {
    await using var scope = _scope();
    var bundle = new RawBundle("Contracts.Job.RowAddedEvent, Contracts", "{\"v\":1}");

    var result = CompositeInboxFanout.TryExpand(bundle, _source(Guid.CreateVersion7()), scope);

    var child = result.Children.Single();
    await Assert.That(child.SourceServiceId).IsEqualTo(_sourceServiceId)
      .Because("with no origin override the child carries the composite's own source service");
  }

  // ---- typed path: origin override --------------------------------------------------------------

  [Test]
  public async Task TryExpand_TypedIdentityCompositeNamingAnOrigin_ChildCarriesTheOriginIdentityAsync() {
    var origin = Guid.CreateVersion7();
    var childId = Guid.CreateVersion7();
    await using var scope = _scope();
    var composite = new OriginTypedComposite(origin, childId, childSequence: 7, new ChildEvent("a"));

    var result = CompositeInboxFanout.TryExpand(composite, _source(Guid.CreateVersion7()), scope);

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    var child = result.Children.Single();
    await Assert.That(child.SourceServiceId).IsEqualTo(origin)
      .Because("a typed re-delivered child is recounted under the origin that emitted it, not the relaying source");
    await Assert.That(child.SourceServiceId).IsNotEqualTo(_sourceServiceId);
    await Assert.That(child.SourceCommitSequence).IsEqualTo(7L)
      .Because("the child keeps its original commit sequence so it recounts inside its original window");
    await Assert.That(child.MessageId).IsEqualTo(childId);
  }
}
