using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Slice 26.6 — RED-first locks for the C# half of the inbox source-identity round-trip.
/// <c>InboxMessage</c> surfaces <c>SourceServiceId</c> / <c>SourceCommitSequence</c> so the
/// transport consumer's JSONB payload carries them into <c>store_inbox_messages</c>.
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
public class TransportConsumerSourceIdentityTests {

  [Test]
  public async Task InboxMessage_HasSourceIdentityFieldsAsync() {
    var sourceServiceId = (Guid)TrackedGuid.NewMedo();
    var envelope = _newEnvelope(payload: JsonDocument.Parse("{}").RootElement);

    var msg = new InboxMessage {
      MessageId = (Guid)TrackedGuid.NewMedo(),
      HandlerName = "Test",
      MessageType = "TestEvent",
      EnvelopeType = "MessageEnvelope`1[[X]]",
      Envelope = envelope,
      SourceServiceId = sourceServiceId,
      SourceCommitSequence = 42L,
    };

    await Assert.That(msg.SourceServiceId).IsEqualTo(sourceServiceId);
    await Assert.That(msg.SourceCommitSequence).IsEqualTo(42L);
  }

  [Test]
  public async Task InboxMessage_DefaultsSourceIdentityWhenOmittedAsync() {
    var envelope = _newEnvelope(payload: JsonDocument.Parse("{}").RootElement);

    var msg = new InboxMessage {
      MessageId = (Guid)TrackedGuid.NewMedo(),
      HandlerName = "Test",
      MessageType = "TestEvent",
      EnvelopeType = "MessageEnvelope`1[[X]]",
      Envelope = envelope,
    };

    await Assert.That(msg.SourceServiceId).IsEqualTo(Guid.Empty)
      .Because("default is zero; consumer-side SQL trigger fills in local service identity");
    await Assert.That(msg.SourceCommitSequence).IsEqualTo(0L);
  }

  [Test]
  public async Task InboxMessage_SerializesSourceIdentityIntoJsonAsync() {
    // The full deserialize round-trip can't go through MessageEnvelope<JsonElement>
    // because IMessageEnvelope is abstract (no type discriminator on the JSON). The
    // production SQL path bypasses this — store_inbox_messages reads each field with
    // `elem->>'SourceServiceId'` etc. (see InboxSourceIdentityRoundtripSqlTests). For
    // the C# layer we only need to confirm the JSON payload CONTAINS the new fields
    // with the right values so the SQL function can pick them up.
    var sourceServiceId = (Guid)TrackedGuid.NewMedo();
    var envelope = _newEnvelope(payload: JsonDocument.Parse("{}").RootElement);
    var original = new InboxMessage {
      MessageId = (Guid)TrackedGuid.NewMedo(),
      HandlerName = "Test",
      MessageType = "TestEvent",
      EnvelopeType = "MessageEnvelope`1[[X]]",
      Envelope = envelope,
      SourceServiceId = sourceServiceId,
      SourceCommitSequence = 42L,
    };

    var options = JsonContextRegistry.CreateCombinedOptions();
    var json = JsonSerializer.Serialize(original, options);

    await Assert.That(json).Contains($"\"SourceServiceId\":\"{sourceServiceId}\"")
      .Because("InboxMessage[] payload must carry SourceServiceId so store_inbox_messages can persist it");
    await Assert.That(json).Contains("\"SourceCommitSequence\":42");
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static MessageEnvelope<JsonElement> _newEnvelope(JsonElement payload) {
    return new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From((Guid)TrackedGuid.NewMedo()),
      Payload = payload,
      Hops = [new MessageHop {
        ServiceInstance = new ServiceInstanceInfo {
          InstanceId = Guid.NewGuid(),
          ServiceName = "test",
          HostName = "host",
          ProcessId = 1
        },
        Timestamp = DateTimeOffset.UtcNow,
        Type = HopType.Current
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }
}
