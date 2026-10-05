// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// The interface-typed envelope contract (<c>MessageEnvelope&lt;IEvent&gt;</c>) is hand-built metadata, and
/// hand-built metadata drops every field it does not name on each round trip. It named only the id, payload,
/// hops and priority, so an envelope that passed through it lost the producer's origin and causality (#1029),
/// its version and dispatch context, its directed target and its state-only marker.
/// </summary>
/// <docs>resilience/stream-integrity#origin-stamp</docs>
public class PolymorphicEnvelopeOriginTests {
  private static readonly Guid _producer = Guid.Parse("2b7c9d1e-4f3a-4b6c-8d2e-1f0a9b8c7d6e");
  private static readonly Guid _causedBy = Guid.Parse("6e5d4c3b-2a19-4f8e-9d7c-6b5a49382716");

  [Test]
  public async Task InterfaceTypedEnvelope_RoundTrips_EveryEnvelopeFieldAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IEvent>(options)!;
    var envelope = new MessageEnvelope<IEvent> {
      MessageId = MessageId.New(),
      Payload = new OriginWireProbeEvent(Guid.Parse("0b6e3c1d-2f4a-4e5b-8c7d-6a5b4c3d2e1f"), "polymorphic"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Both, Source = MessageSource.Outbox, IsDefaultDispatch = true },
      Version = 2,
      SourceServiceId = _producer,
      SourceCommitSequence = 21,
      CausedByServiceId = _causedBy,
      CausedByCommitSequence = 8,
      Target = "one-consumer",
      StateOnly = true,
      Priority = 60,
    };

    var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(envelope, typeInfo), typeInfo)!;

    await Assert.That(back.Payload).IsTypeOf<OriginWireProbeEvent>();
    await Assert.That(back.SourceServiceId).IsEqualTo(_producer);
    await Assert.That(back.SourceCommitSequence).IsEqualTo(21L);
    await Assert.That(back.CausedByServiceId).IsEqualTo(_causedBy);
    await Assert.That(back.CausedByCommitSequence).IsEqualTo(8L);
    await Assert.That(back.Version).IsEqualTo(2);
    await Assert.That(back.DispatchContext.Mode).IsEqualTo(DispatchModes.Both);
    await Assert.That(back.DispatchContext.IsDefaultDispatch).IsTrue();
    await Assert.That(back.Target).IsEqualTo("one-consumer");
    await Assert.That(back.StateOnly).IsTrue();
    await Assert.That(back.Priority).IsEqualTo(60);
  }

  [Test]
  public async Task InterfaceTypedEnvelope_WrittenBeforeTheFields_ReadsWithUnstampedDefaultsAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = JsonContextRegistry.GetPolymorphicEnvelopeTypeInfo<IEvent>(options)!;
    var envelope = new MessageEnvelope<IEvent>(MessageId.New(),
      new OriginWireProbeEvent(Guid.Parse("0b6e3c1d-2f4a-4e5b-8c7d-6a5b4c3d2e1f"), "older"), []);
    var node = JsonNode.Parse(JsonSerializer.Serialize(envelope, typeInfo))!.AsObject();
    foreach (var name in new[] { "Version", "DispatchContext", "SourceServiceId", "SourceCommitSequence", "CausedByServiceId", "CausedByCommitSequence" }) {
      node.Remove(name);
    }

    var back = JsonSerializer.Deserialize(node.ToJsonString(), typeInfo)!;

    await Assert.That(back.SourceServiceId).IsEqualTo(Guid.Empty);
    await Assert.That(back.SourceCommitSequence).IsEqualTo(0L);
    await Assert.That(back.CausedByServiceId).IsNull();
    await Assert.That(back.Version).IsEqualTo(1)
      .Because("a body without a version is a v1 envelope, never version zero");
    await Assert.That(back.DispatchContext.Mode).IsEqualTo(DispatchModes.Outbox);
    await Assert.That(back.Target).IsNull();
    await Assert.That(back.StateOnly).IsFalse();
  }
}
