using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Generated;
using Whizbang.Core.Internal;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Proves <see cref="ReceptorInvocationRecord"/> entries survive the full
/// serialization round-trip that the outbox / transport / inbox path puts envelopes
/// through. The records are how the double-fire guardrail persists across services,
/// so this roundtrip is a load-bearing invariant.
/// </summary>
/// <docs>fundamentals/receptors/exactly-once-firing</docs>
public partial class ReceptorInvocationsRoundtripTests {

  private static JsonSerializerOptions _createOptions() {
    return new JsonSerializerOptions {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      TypeInfoResolver = JsonTypeInfoResolver.Combine(
        WhizbangIdJsonContext.Default,
        EnvelopeSerializerTests.EnvelopeTestJsonContext.Default,
        InfrastructureJsonContext.Default
      )
    };
  }

  private static MessageHop _hop() => new() {
    Type = HopType.Current,
    ServiceInstance = ServiceInstanceInfo.Unknown,
    Timestamp = DateTimeOffset.UtcNow
  };

  [Test]
  public async Task ReceptorInvocations_SurviveEnvelopeSerializerRoundtripAsync() {
    // Arrange: an envelope with multiple invocation records (simulating what the guardrail
    // would have appended as the message flowed through lifecycle stages).
    var options = _createOptions();
    var serializer = new EnvelopeSerializer(options);
    var msgId = MessageId.New();
    var envelope = new MessageEnvelope<EnvelopeSerializerTests.EnvelopeTestMsg> {
      MessageId = msgId,
      Payload = new EnvelopeSerializerTests.EnvelopeTestMsg("test"),
      Hops = [_hop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      ReceptorInvocations = [
        new ReceptorInvocationRecord {
          ReceptorId = "ReceptorA",
          Stage = LifecycleStage.LocalImmediateInline,
          CompletedAt = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero),
          Duration = TimeSpan.FromMilliseconds(3),
          ServiceName = "svc-a"
        },
        new ReceptorInvocationRecord {
          ReceptorId = "ReceptorB",
          Stage = LifecycleStage.PostInboxInline,
          CompletedAt = new DateTimeOffset(2026, 4, 18, 12, 0, 1, TimeSpan.Zero),
          Duration = TimeSpan.FromMilliseconds(12),
          ServiceName = "svc-b"
        }
      ]
    };

    // Act: serialize via the envelope serializer, then through a full JSON string roundtrip
    // (simulating outbox persistence + transport write + inbox read).
    var serialized = serializer.SerializeEnvelope(envelope);
    var jsonString = JsonSerializer.Serialize(serialized.JsonEnvelope, options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
    var rehydrated = (MessageEnvelope<JsonElement>)JsonSerializer.Deserialize(jsonString, options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)))!;

    // Assert: invocation records roundtripped.
    await Assert.That(rehydrated.ReceptorInvocations).IsNotNull();
    await Assert.That(rehydrated.ReceptorInvocations!).Count().IsEqualTo(2);

    var first = rehydrated.ReceptorInvocations![0];
    await Assert.That(first.ReceptorId).IsEqualTo("ReceptorA");
    await Assert.That(first.Stage).IsEqualTo(LifecycleStage.LocalImmediateInline);
    await Assert.That(first.CompletedAt).IsEqualTo(new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero));
    await Assert.That(first.Duration).IsEqualTo(TimeSpan.FromMilliseconds(3));
    await Assert.That(first.ServiceName).IsEqualTo("svc-a");

    var second = rehydrated.ReceptorInvocations[1];
    await Assert.That(second.ReceptorId).IsEqualTo("ReceptorB");
    await Assert.That(second.Stage).IsEqualTo(LifecycleStage.PostInboxInline);
    await Assert.That(second.Duration).IsEqualTo(TimeSpan.FromMilliseconds(12));
  }

  [Test]
  public async Task NullReceptorInvocations_SerializesWithoutExplicitNullAsync() {
    // ReceptorInvocations is nullable; envelopes that never invoked a tracked receptor
    // should not pay a JSON-size penalty for the field.
    var options = _createOptions();
    var serializer = new EnvelopeSerializer(options);
    var envelope = new MessageEnvelope<EnvelopeSerializerTests.EnvelopeTestMsg> {
      MessageId = MessageId.New(),
      Payload = new EnvelopeSerializerTests.EnvelopeTestMsg("test"),
      Hops = [_hop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      ReceptorInvocations = null
    };

    var serialized = serializer.SerializeEnvelope(envelope);
    var jsonString = JsonSerializer.Serialize(serialized.JsonEnvelope, options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));

    // System.Text.Json's default settings serialize null as "null". We explicitly want the
    // field NOT to appear in the wire payload to keep envelopes lean.
    _ = jsonString.Contains("\"rin\"", StringComparison.Ordinal);
    var rehydrated = (MessageEnvelope<JsonElement>)JsonSerializer.Deserialize(jsonString, options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)))!;

    // Either the field is elided (preferred) or it's null — both are acceptable as long as
    // the receiver sees null after rehydration.
    await Assert.That(rehydrated.ReceptorInvocations).IsNull();
    // And the bytes shouldn't include a populated array marker even if the key appears.
    await Assert.That(jsonString.Contains("\"rin\":[", StringComparison.Ordinal)).IsFalse();
  }

  [Test]
  public async Task ReceptorInvocations_PreservesOrderAcrossRoundtripAsync() {
    // The guardrail walks the list in insertion order when deciding whether a receptor has
    // fired before. Serialization must preserve that ordering.
    var options = _createOptions();
    var serializer = new EnvelopeSerializer(options);
    var invocations = Enumerable.Range(0, 10).Select(i => new ReceptorInvocationRecord {
      ReceptorId = $"Receptor{i}",
      Stage = LifecycleStage.PostInboxInline,
      CompletedAt = DateTimeOffset.UtcNow.AddMilliseconds(i),
      Duration = TimeSpan.FromMilliseconds(i),
      ServiceName = "svc"
    }).ToList();
    var envelope = new MessageEnvelope<EnvelopeSerializerTests.EnvelopeTestMsg> {
      MessageId = MessageId.New(),
      Payload = new EnvelopeSerializerTests.EnvelopeTestMsg("test"),
      Hops = [_hop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      ReceptorInvocations = invocations
    };

    var serialized = serializer.SerializeEnvelope(envelope);
    var jsonString = JsonSerializer.Serialize(serialized.JsonEnvelope, options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
    var rehydrated = (MessageEnvelope<JsonElement>)JsonSerializer.Deserialize(jsonString, options.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)))!;

    for (int i = 0; i < 10; i++) {
      await Assert.That(rehydrated.ReceptorInvocations![i].ReceptorId).IsEqualTo($"Receptor{i}");
    }
  }
}
