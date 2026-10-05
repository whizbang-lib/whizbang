// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Observability;

/// <summary>A received event for the origin wire suite; public and top-level so the Whizbang JSON generator emits its typed envelope contract.</summary>
public sealed record OriginWireProbeEvent([property: StreamId] Guid ProbeId, string Note) : IEvent;

/// <summary>
/// A received event names the service that produced it (#1029). The producer's outbox stamps the wire
/// envelope with its service id (<c>sid</c>) and commit sequence (<c>sseq</c>); the receiver binds the bytes
/// through the generated typed envelope contract, converts them to the storage form, and stores an inbox row
/// whose source columns the database copies onto the stored event as <c>origin_service_id</c>. The generated
/// contract named only id, payload, hops, target, state-only and priority, so the producer's identity was
/// lost on every ordinary delivery: the row defaulted to the receiver's own id and the event was stored as
/// locally originated, invisible to gap detection, audit and checkpoints.
/// </summary>
/// <docs>resilience/stream-integrity#origin-stamp</docs>
public class EnvelopeOriginWireTests {
  private static readonly Guid _producer = Guid.Parse("8a1f6f7e-4a52-4c0e-9d5b-2f0d3c1b9a11");
  private static readonly Guid _causedBy = Guid.Parse("4c3b2a19-0f8e-4d7c-a6b5-9e8d7c6b5a49");

  private static MessageEnvelope<OriginWireProbeEvent> _typed() => new() {
    MessageId = MessageId.New(),
    Payload = new OriginWireProbeEvent(Guid.Parse("0b6e3c1d-2f4a-4e5b-8c7d-6a5b4c3d2e1f"), "hello"),
    Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UnixEpoch, ServiceInstance = ServiceInstanceInfo.Unknown }],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Both, Source = MessageSource.Outbox },
  };

  /// <summary>
  /// The wire as a producer's outbox drain writes it: the storage form, rebuilt with the origin stamped,
  /// serialized through the attribute-honoring <see cref="MessageEnvelope{JsonElement}"/> shape.
  /// </summary>
  private static (string WireJson, string EnvelopeType) _producerWire(JsonSerializerOptions options) {
    var serialized = new EnvelopeSerializer(options).SerializeEnvelope(_typed());
    var stored = serialized.JsonEnvelope;
    var wire = new MessageEnvelope<JsonElement> {
      MessageId = stored.MessageId,
      Payload = stored.Payload,
      Hops = stored.Hops,
      DispatchContext = stored.DispatchContext,
      Version = stored.Version,
      SourceServiceId = _producer,
      SourceCommitSequence = 7,
      CausedByServiceId = _causedBy,
      CausedByCommitSequence = 3,
      Priority = 40,
    };
    var json = JsonSerializer.Serialize(wire, options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
    return (json, serialized.EnvelopeType);
  }

  private static MessageEnvelope<OriginWireProbeEvent> _receive(string wireJson, string envelopeType, JsonSerializerOptions options) {
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(envelopeType, options)
      ?? throw new InvalidOperationException($"No typed contract for {envelopeType}");
    return (MessageEnvelope<OriginWireProbeEvent>)JsonSerializer.Deserialize(wireJson, typeInfo)!;
  }

  [Test]
  public async Task TypedReceive_ThroughTheGeneratedContract_CarriesTheProducersOriginAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var (wireJson, envelopeType) = _producerWire(options);

    var received = _receive(wireJson, envelopeType, options);

    await Assert.That(received.SourceServiceId).IsEqualTo(_producer)
      .Because("the producer's service id rides the wire as sid; dropping it stores every received event as locally originated");
    await Assert.That(received.SourceCommitSequence).IsEqualTo(7L)
      .Because("the producer's commit sequence (sseq) is what gap detection counts against its checkpoints");
    await Assert.That(received.CausedByServiceId).IsEqualTo(_causedBy);
    await Assert.That(received.CausedByCommitSequence).IsEqualTo(3L);
    await Assert.That(received.DispatchContext.Mode).IsEqualTo(DispatchModes.Both)
      .Because("the dispatch context (dc) is on the wire and the typed receive must not replace it with the v1 default");
    await Assert.That(received.DispatchContext.Source).IsEqualTo(MessageSource.Outbox);
    await Assert.That(received.Version).IsEqualTo(1);
    await Assert.That(received.Priority).IsEqualTo(40);
    await Assert.That(received.Payload.Note).IsEqualTo("hello");
  }

  [Test]
  public async Task ReceivedInboxRow_FromTheWire_NamesTheProducerAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var (wireJson, envelopeType) = _producerWire(options);
    var received = _receive(wireJson, envelopeType, options);

    // The consumer workers' storage-form conversion, then the shared row builder (#739).
    var jsonEnvelope = new EnvelopeSerializer(options).SerializeEnvelope(received).JsonEnvelope;
    var row = ReceivedInboxMessageBuilder.Build(
      new ReceivedInboxMessageBuilder.ReceivedEnvelope(received, jsonEnvelope, envelopeType,
        TypeNameFormatter.AssemblyQualifiedNameOrNull(typeof(OriginWireProbeEvent))!, IsEvent: true),
      priority: 40, guardSite: "test", eventMarkerResolver: null, ephemeralModeResolver: null);

    await Assert.That(row.SourceServiceId).IsEqualTo(_producer)
      .Because("a zero here makes the store stamp the receiver's own id, and the emit chain then leaves origin_service_id NULL");
    await Assert.That(row.SourceCommitSequence).IsEqualTo(7L);
    await Assert.That(row.Envelope.SourceServiceId).IsEqualTo(_producer)
      .Because("the stored envelope keeps the producer's identity, so a later read or forward of the row still names it");
    await Assert.That(row.Envelope.SourceCommitSequence).IsEqualTo(7L);
  }

  [Test]
  public async Task TypedEnvelope_RoundTripsThroughTheGeneratedContract_WithOriginIntactAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = options.GetTypeInfo(typeof(MessageEnvelope<OriginWireProbeEvent>));
    var typed = new MessageEnvelope<OriginWireProbeEvent> {
      MessageId = MessageId.New(),
      Payload = new OriginWireProbeEvent(Guid.Parse("0b6e3c1d-2f4a-4e5b-8c7d-6a5b4c3d2e1f"), "typed"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox, IsDefaultDispatch = true },
      Version = 2,
      SourceServiceId = _producer,
      SourceCommitSequence = 11,
      CausedByServiceId = _causedBy,
      CausedByCommitSequence = 5,
    };

    var json = JsonSerializer.Serialize(typed, typeInfo);
    var back = (MessageEnvelope<OriginWireProbeEvent>)JsonSerializer.Deserialize(json, typeInfo)!;

    await Assert.That(back.SourceServiceId).IsEqualTo(_producer);
    await Assert.That(back.SourceCommitSequence).IsEqualTo(11L);
    await Assert.That(back.CausedByServiceId).IsEqualTo(_causedBy);
    await Assert.That(back.CausedByCommitSequence).IsEqualTo(5L);
    await Assert.That(back.Version).IsEqualTo(2);
    await Assert.That(back.DispatchContext.Mode).IsEqualTo(DispatchModes.Outbox);
    await Assert.That(back.DispatchContext.IsDefaultDispatch).IsTrue();
    await Assert.That(JsonNode.Parse(json)!["sid"]!.GetValue<Guid>()).IsEqualTo(_producer)
      .Because("the typed contract writes the same wire names as the attribute-honoring shape");
  }

  [Test]
  public async Task EnvelopeSerializer_StorageForm_KeepsTheOriginAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var typed = new MessageEnvelope<OriginWireProbeEvent> {
      MessageId = MessageId.New(),
      Payload = new OriginWireProbeEvent(Guid.Parse("0b6e3c1d-2f4a-4e5b-8c7d-6a5b4c3d2e1f"), "stored"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Version = 2,
      SourceServiceId = _producer,
      SourceCommitSequence = 9,
      CausedByServiceId = _causedBy,
      CausedByCommitSequence = 4,
    };

    var stored = new EnvelopeSerializer(options).SerializeEnvelope(typed).JsonEnvelope;

    await Assert.That(stored.SourceServiceId).IsEqualTo(_producer);
    await Assert.That(stored.SourceCommitSequence).IsEqualTo(9L);
    await Assert.That(stored.CausedByServiceId).IsEqualTo(_causedBy);
    await Assert.That(stored.CausedByCommitSequence).IsEqualTo(4L);
    await Assert.That(stored.Version).IsEqualTo(2);
  }

  [Test]
  public async Task ReconstructWithPayload_BothOverloads_KeepTheOriginAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var (wireJson, _) = _producerWire(options);
    var jsonEnvelope = (MessageEnvelope<JsonElement>)JsonSerializer.Deserialize(wireJson, options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)))!;
    var payload = new OriginWireProbeEvent(Guid.Parse("0b6e3c1d-2f4a-4e5b-8c7d-6a5b4c3d2e1f"), "rebuilt");

    var untyped = jsonEnvelope.ReconstructWithPayload(payload, "Handler");
    var typed = jsonEnvelope.ReconstructWithPayload<OriginWireProbeEvent>(payload);

    foreach (var rebuilt in new IMessageEnvelope[] { untyped, typed }) {
      await Assert.That(rebuilt.SourceServiceId).IsEqualTo(_producer);
      await Assert.That(rebuilt.SourceCommitSequence).IsEqualTo(7L);
      await Assert.That(rebuilt.CausedByServiceId).IsEqualTo(_causedBy);
      await Assert.That(rebuilt.CausedByCommitSequence).IsEqualTo(3L);
      await Assert.That(rebuilt.Version).IsEqualTo(1);
    }
  }

  // ── Wire compatibility in both directions ────────────────────────────────────────────────

  [Test]
  public async Task OldSender_WithoutOriginFields_IsReceivedWithTheUnstampedDefaultsAsync() {
    // A sender from before the outbox stamped origins (or one whose service id never resolved) writes no
    // sid/sseq/cbid/cbseq, and the oldest wire carries no v or dc either. The receive must not fail, and the
    // row must stay unstamped so the store keeps today's behavior: origin_service_id stays NULL.
    var options = JsonContextRegistry.CreateCombinedOptions();
    var (wireJson, envelopeType) = _producerWire(options);
    var node = JsonNode.Parse(wireJson)!.AsObject();
    foreach (var name in new[] { "sid", "sseq", "cbid", "cbseq", "v", "dc" }) {
      node.Remove(name);
    }

    var received = _receive(node.ToJsonString(), envelopeType, options);
    var jsonEnvelope = new EnvelopeSerializer(options).SerializeEnvelope(received).JsonEnvelope;
    var row = ReceivedInboxMessageBuilder.Build(
      new ReceivedInboxMessageBuilder.ReceivedEnvelope(received, jsonEnvelope, envelopeType,
        TypeNameFormatter.AssemblyQualifiedNameOrNull(typeof(OriginWireProbeEvent))!, IsEvent: true),
      priority: 40, guardSite: "test", eventMarkerResolver: null, ephemeralModeResolver: null);

    await Assert.That(received.SourceServiceId).IsEqualTo(Guid.Empty);
    await Assert.That(received.SourceCommitSequence).IsEqualTo(0L);
    await Assert.That(received.CausedByServiceId).IsNull();
    await Assert.That(received.CausedByCommitSequence).IsNull();
    await Assert.That(received.Version).IsEqualTo(1)
      .Because("a missing v is the v1 envelope, not version zero");
    await Assert.That(received.DispatchContext.Mode).IsEqualTo(DispatchModes.Outbox)
      .Because("a missing dc takes the v1 default the constructor documents");
    await Assert.That(received.DispatchContext.Source).IsEqualTo(MessageSource.Local);
    await Assert.That(row.SourceServiceId).IsEqualTo(Guid.Empty)
      .Because("an unstamped row lets the store fall back exactly as it did before the fix");
  }

  [Test]
  public async Task NewSender_ToAReceiverWithThePreFixContract_StillDeserializesAsync() {
    // The receiver from before this fix binds through a contract that names only id, p and h (plus the
    // optional tgt/sto/pri). The origin fields were already on the wire, written by the outbox drain, so an
    // old receiver has always skipped them; this pins that the skip is the serializer's default and not luck.
    var options = JsonContextRegistry.CreateCombinedOptions();
    var (wireJson, _) = _producerWire(options);

    var legacy = JsonSerializer.Deserialize(wireJson, _preFixReceiveContract(options))!;

    await Assert.That(legacy.Payload.Note).IsEqualTo("hello");
    await Assert.That(legacy.SourceServiceId).IsEqualTo(Guid.Empty)
      .Because("the old receiver ignores sid and stores unstamped, exactly as before");
  }

  /// <summary>The typed receive contract as the generator emitted it before #1029: id, payload and hops only.</summary>
  private static JsonTypeInfo<MessageEnvelope<OriginWireProbeEvent>> _preFixReceiveContract(JsonSerializerOptions options) {
    var properties = new JsonPropertyInfo[] {
      _property<MessageId>(options, "MessageId", "id", e => e.MessageId),
      _property<OriginWireProbeEvent>(options, "Payload", "p", e => e.Payload),
      _property<List<MessageHop>>(options, "Hops", "h", e => e.Hops),
    };
    var ctorParams = new JsonParameterInfoValues[] {
      new() { Name = "messageId", ParameterType = typeof(MessageId), Position = 0 },
      new() { Name = "payload", ParameterType = typeof(OriginWireProbeEvent), Position = 1 },
      new() { Name = "hops", ParameterType = typeof(List<MessageHop>), Position = 2 },
    };
    var objectInfo = new JsonObjectInfoValues<MessageEnvelope<OriginWireProbeEvent>> {
      ObjectWithParameterizedConstructorCreator = static args => new MessageEnvelope<OriginWireProbeEvent>(
        (MessageId)args[0], (OriginWireProbeEvent)args[1], (List<MessageHop>)args[2]),
      PropertyMetadataInitializer = _ => properties,
      ConstructorParameterMetadataInitializer = () => ctorParams,
    };
    return JsonMetadataServices.CreateObjectInfo(options, objectInfo);
  }

  private static JsonPropertyInfo _property<T>(
      JsonSerializerOptions options, string name, string wireName, Func<MessageEnvelope<OriginWireProbeEvent>, T> getter) =>
    JsonMetadataServices.CreatePropertyInfo(options, new JsonPropertyInfoValues<T> {
      IsProperty = true,
      IsPublic = true,
      DeclaringType = typeof(MessageEnvelope<OriginWireProbeEvent>),
      Getter = obj => getter((MessageEnvelope<OriginWireProbeEvent>)obj),
      PropertyName = name,
      JsonPropertyName = wireName,
    });
}
