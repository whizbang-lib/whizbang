// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tags;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #1628: a coalesced composite carries each folded single's position in the producer's commit lane, so every child it
/// fans out into carries the <c>SourceCommitSequence</c> a direct delivery of that single would have carried. Without it
/// every child copied the composite's own sequence, which is zero (a composite has no event-store row), so origin-order
/// replay fell back to stored order for any stream holding a fanned-out child and integrity accounting never counted one.
/// </summary>
/// <remarks>
/// The field is additive on the wire: an older receiver skips it (the combined options skip unknown members), and a
/// composite from an older producer reads back without it, so its children carry the composite's sequence as before.
/// Both directions are asserted here.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Minting/CoalescedEventsComposite.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/CoalesceShipWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/CompositeInboxFanout.cs</code-under-test>
[Category("Messaging")]
public class CoalescedChildOriginSequenceTests {
  private const string WIRE_TYPE = "Contracts.RecordCaptured, Contracts";
  private static readonly Guid _producer = Guid.Parse("0193a1b2-0000-7000-8000-000000000001");

  [Test]
  public async Task BuildDefaultComposite_CarriesEachSinglesCommitSequenceInFoldOrderAsync() {
    var composite = _fold(41, null, 43);

    await Assert.That(composite.InnerCommitSequences).IsNotNull();
    await Assert.That(composite.InnerCommitSequences).IsEquivalentTo(new long?[] { 41, null, 43 })
      .Because("the list is parallel to the inner payloads: a single with no position keeps its slot as null");
  }

  [Test]
  public async Task TryExpand_CoalescedComposite_EachChildCarriesItsOwnSinglesSequenceAsync() {
    var composite = _fold(41, null, 43);

    var result = CompositeInboxFanout.TryExpand(composite, _compositeDelivery(composite.StreamId), _provider());

    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    var stored = result.Children.Select(c => c.SourceCommitSequence).ToList();
    var carried = result.Children.Select(c => c.Envelope.SourceCommitSequence).ToList();
    await Assert.That(stored).IsEquivalentTo(new long[] { 41, 0, 43 })
      .Because("a child carries its single's position; one with none carries the composite's, which is zero");
    await Assert.That(carried).IsEquivalentTo(new long[] { 41, 0, 43 })
      .Because("the child envelope is what replay reads its origin order from");
    foreach (var child in result.Children) {
      await Assert.That(child.SourceServiceId).IsEqualTo(_producer)
        .Because("the positions are in the producer's own lane, the service the composite came from");
    }
  }

  [Test]
  public async Task CoalescedComposite_InnerCommitSequences_CrossTheWireAsync() {
    var typeInfo = JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(CoalescedEventsComposite));
    var composite = _fold(7, null, 9);

    var back = (CoalescedEventsComposite)JsonSerializer.Deserialize(JsonSerializer.Serialize(composite, typeInfo), typeInfo)!;

    await Assert.That(back.InnerCommitSequences).IsEquivalentTo(new long?[] { 7, null, 9 });
  }

  /// <summary>A newer receiver given a composite from an older producer, which never wrote the field.</summary>
  [Test]
  public async Task OlderProducersComposite_ReadsWithNoSequences_AndChildrenCarryTheCompositesAsync() {
    var typeInfo = JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(CoalescedEventsComposite));
    var node = JsonNode.Parse(JsonSerializer.Serialize(_fold(7, 8), typeInfo))!.AsObject();
    await Assert.That(node.Remove(nameof(CoalescedEventsComposite.InnerCommitSequences))).IsTrue()
      .Because("the field must be on the wire under this name for the older shape to be the one without it");

    var older = (CoalescedEventsComposite)JsonSerializer.Deserialize(node.ToJsonString(), typeInfo)!;
    var source = _compositeDelivery(older.StreamId, sequence: 5);
    var result = CompositeInboxFanout.TryExpand(older, source, _provider());

    await Assert.That(older.InnerCommitSequences).IsNull();
    await Assert.That(result.Outcome).IsEqualTo(CompositeInboxFanout.FanoutOutcome.Expanded);
    await Assert.That(result.Children.Select(c => c.SourceCommitSequence).ToList()).IsEquivalentTo(new long[] { 5, 5 })
      .Because("with no per-child positions a child carries the composite's sequence, exactly as before the field");
  }

  /// <summary>
  /// An older receiver given a composite from a newer producer: its type has no such member, and the combined options
  /// skip a member a type does not have instead of failing the read. Asserted with a member no build has.
  /// </summary>
  [Test]
  public async Task OlderReceiver_SkipsAMemberItsCompositeTypeDoesNotHaveAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = options.GetTypeInfo(typeof(CoalescedEventsComposite));
    var composite = _fold(7);
    var node = JsonNode.Parse(JsonSerializer.Serialize(composite, typeInfo))!.AsObject();
    node["InnerSomethingNoBuildHas"] = new JsonArray(1, 2);

    var back = (CoalescedEventsComposite)JsonSerializer.Deserialize(node.ToJsonString(), typeInfo)!;

    await Assert.That(options.UnmappedMemberHandling).IsEqualTo(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip);
    await Assert.That(back.InnerEventIds).IsEquivalentTo(composite.InnerEventIds);
  }

  /// <summary>
  /// Audit records are not stored as events on the producer, so they have no position to carry; the audit composite
  /// carries none, and its children keep the composite's sequence, as a direct delivery of a record does.
  /// </summary>
  [Test]
  public async Task AuditComposite_CarriesNoSequencesAsync() {
    IIdentityPreservingComposite composite = (AuditEventsComposite)SystemEventCoalesceDefaults.BuildAuditComposite(new CoalesceFoldBatch {
      Group = "audit",
      Singles = [_single(41)],
      Atomicity = FanoutAtomicity.Independent,
    });

    await Assert.That(composite.InnerCommitSequences).IsNull();
  }

  private static CoalescedEventsComposite _fold(params long?[] sequences) =>
    (CoalescedEventsComposite)CoalesceShipWorker.BuildDefaultComposite(new CoalesceFoldBatch {
      Group = "record-digest",
      Singles = [.. sequences.Select(_single)],
      Atomicity = FanoutAtomicity.Independent,
    });

  private static OutboxMessage _single(long? sequence) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonElement.Parse("""{"Name":"single"}"""),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    return new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "records",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
      EnvelopeType = EnvelopeTypeNameHelper.Format(WIRE_TYPE),
      StreamId = TrackedGuid.New(),
      IsEvent = true,
      MessageType = WIRE_TYPE,
      SourceCommitSequence = sequence,
    };
  }

  /// <summary>The composite as a receiver gets it: drained by its producer with no position of its own.</summary>
  private static MessageEnvelope<JsonElement> _compositeDelivery(Guid streamId, long sequence = 0) => new() {
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    MessageId = MessageId.New(),
    Payload = JsonElement.Parse("{}"),
    Hops = [new MessageHop {
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UnixEpoch,
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Metadata = StreamIdentity.HopMetadata(streamId),
    }],
    SourceServiceId = _producer,
    SourceCommitSequence = sequence,
  };

  private static ServiceProvider _provider() =>
    new ServiceCollection()
      .AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()))
      .BuildServiceProvider();
}
