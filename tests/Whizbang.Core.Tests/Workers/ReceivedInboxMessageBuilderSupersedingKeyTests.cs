// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A received superseding event is stored on the receiver with the flag and the key its producer stored it with
/// (#1729): the payload arrives as JSON, so the flag comes from the catalog by name and the key from the payload read as
/// its registered type. The receiver reaps its own copy by them.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ReceivedInboxMessageBuilder.cs</code-under-test>
/// <docs>fundamentals/events/superseding-events#across-services</docs>
public class ReceivedInboxMessageBuilderSupersedingKeyTests {
  [Superseding]
  public sealed record ReceivedEmbedding([property: StreamId] Guid DocumentId, [property: SupersedingKey] string Model) : IEvent;

  [Superseding]
  public sealed record ReceivedRegionAssigned([property: StreamId] Guid DocumentId, [property: SupersedingKey] Guid RegionId) : IEvent;

  [Superseding(Group = "received-region")]
  public sealed record ReceivedRegionRevised([property: StreamId] Guid DocumentId, [property: SupersedingKey] Guid RegionId) : IEvent;

  public sealed record ReceivedOrdinary([property: StreamId] Guid DocumentId) : IEvent;

  private static readonly Guid _document = Guid.Parse("0199a1b2-0000-7000-8000-000000000201");

  private static string _name(Type type) => type.FullName + ", " + type.Assembly.GetName().Name;

  private static InboxMessage _build(object payload, Type type, JsonSerializerOptions? options) {
    var serializerOptions = JsonContextRegistry.CreateCombinedOptions();
    return _buildJson(JsonSerializer.SerializeToElement(payload, serializerOptions.GetTypeInfo(type)), type, options);
  }

  private static InboxMessage _buildJson(JsonElement payload, Type type, JsonSerializerOptions? options) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(TrackedGuid.New()),
      Payload = payload,
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
        ServiceInstance = new ServiceInstanceInfo {
          InstanceId = Guid.Parse("0198f5d2-0000-7000-8000-000000000002"), ServiceName = "producer", HostName = "h", ProcessId = 1,
        },
        StreamId = _document.ToString(),
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    };
    var name = _name(type);
    return ReceivedInboxMessageBuilder.Build(
      new ReceivedInboxMessageBuilder.ReceivedEnvelope(envelope, envelope, EnvelopeTypeNameHelper.Format(name), name, IsEvent: true),
      priority: 0,
      "TransportConsumer.Inbox",
      new EventMarkerResolver(new Whizbang.Core.Tests.Generated.GeneratedMessageTypeCatalog()),
      ephemeralModeResolver: null,
      jsonOptions: options);
  }

  [Test]
  public async Task Build_AReceivedSupersedingEvent_IsFlaggedAndKeyed_AnOrdinaryEventIsNotAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();

    var superseding = _build(new ReceivedEmbedding(_document, "model-a"), typeof(ReceivedEmbedding), options);
    var ordinary = _build(new ReceivedOrdinary(_document), typeof(ReceivedOrdinary), options);

    await Assert.That(superseding.Flags).IsEqualTo(EventFlags.Superseding)
      .Because("the catalog generator stamps IsSuperseding on the entry the receive path resolves by name");
    await Assert.That(superseding.Metadata!.SupersedingKey).IsEqualTo("model-a")
      .Because("the inbox emit chain stamps the receiver's copy with the key read from the received payload");
    await Assert.That(ordinary.Flags).IsEqualTo(EventFlags.None);
    await Assert.That(ordinary.Metadata!.SupersedingKey).IsNull();
  }

  [Test]
  public async Task Build_WithNoOptionsGiven_ReadsTheKeyThroughTheRegisteredContextsAsync() {
    var superseding = _build(new ReceivedEmbedding(_document, "model-c"), typeof(ReceivedEmbedding), options: null);

    await Assert.That(superseding.Metadata!.SupersedingKey).IsEqualTo("model-c");
  }

  /// <summary>
  /// #2123: during a rolling deploy a message from an older producer can lack a key property the receiver's type now
  /// declares. Its key is unknown, so the receiver's copy is flagged but unkeyed and never reaped; a key the message does
  /// carry is a key even when it is the type's default.
  /// </summary>
  [Test]
  public async Task Build_AReceivedEventMissingItsValueTypeKey_IsFlaggedButUnkeyed_APresentDefaultIsAKeyAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();

    var missing = _buildJson(JsonElement.Parse($$"""{"DocumentId":"{{_document}}"}"""), typeof(ReceivedRegionAssigned), options);
    var presentDefault = _buildJson(JsonElement.Parse($$"""{"DocumentId":"{{_document}}","RegionId":"{{Guid.Empty}}"}"""),
      typeof(ReceivedRegionAssigned), options);

    await Assert.That(missing.Flags).IsEqualTo(EventFlags.Superseding);
    await Assert.That(missing.Metadata!.SupersedingKey).IsNull()
      .Because("a key the producer never sent is unknown, not the Guid default every such message would share");
    await Assert.That(presentDefault.Metadata!.SupersedingKey).IsEqualTo(Guid.Empty.ToString("D"))
      .Because("a default value the message does carry is a real key");
  }

  /// <summary>#2124: the receiver's copy of a grouped event is stamped with the group the producer stamped.</summary>
  [Test]
  public async Task Build_AReceivedGroupedEvent_CarriesItsGroup_AnUngroupedOneNoneAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();

    var grouped = _build(new ReceivedRegionRevised(_document, Guid.Empty), typeof(ReceivedRegionRevised), options);
    var ungrouped = _build(new ReceivedEmbedding(_document, "model-a"), typeof(ReceivedEmbedding), options);

    await Assert.That(grouped.Metadata!.SupersedingGroup).IsEqualTo("received-region");
    await Assert.That(ungrouped.Metadata!.SupersedingGroup).IsNull();
  }
}
