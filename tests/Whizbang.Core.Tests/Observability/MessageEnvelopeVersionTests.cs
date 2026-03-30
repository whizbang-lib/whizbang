using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Tests for MessageEnvelope versioning and DispatchContext serialization.
/// Verifies backward compatibility with v1 envelopes and correct round-trip
/// of the new Version and DispatchContext fields.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/MessageEnvelope.cs</code-under-test>
/// <docs>fundamentals/dispatcher/routing#envelope-versioning</docs>
public class MessageEnvelopeVersionTests {

  private static readonly JsonSerializerOptions _jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
  private sealed record TestPayload(string Value);

  // ========================================
  // Version defaults
  // ========================================

  [Test]
  public async Task NewEnvelope_DefaultsToVersion1Async() {
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    await Assert.That(envelope.Version).IsEqualTo(1);
  }

  // ========================================
  // DispatchContext on new envelopes
  // ========================================

  [Test]
  public async Task NewEnvelope_DispatchContext_IsSetAsync() {
    var context = new MessageDispatchContext { Mode = DispatchModes.Both, Source = MessageSource.Outbox };
    var envelope = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("test"),
      Hops = [],
      DispatchContext = context
    };

    await Assert.That(envelope.DispatchContext.Mode).IsEqualTo(DispatchModes.Both);
    await Assert.That(envelope.DispatchContext.Source).IsEqualTo(MessageSource.Outbox);
  }

  // ========================================
  // V1 backward compatibility (JSON deserialization without DispatchContext)
  // ========================================

  [Test]
  public async Task V1Envelope_DeserializesWithDefaultDispatchContextAsync() {
    // v1 JSON: no "v" or "dc" fields
    var json = """
    {
      "id": "019d0000-0000-7000-0000-000000000001",
      "p": { "Value": "from-v1" },
      "h": []
    }
    """;

    // Use JsonElement payload — always supported in AOT mode (TestPayload isn't registered)
    var envelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(json, _jsonOptions);

    await Assert.That(envelope).IsNotNull();
    await Assert.That(envelope!.Version).IsEqualTo(1); // default from constructor
    await Assert.That(envelope.DispatchContext).IsNotNull();
    await Assert.That(envelope.DispatchContext.Mode).IsEqualTo(DispatchModes.Outbox); // v1 fallback
    await Assert.That(envelope.DispatchContext.Source).IsEqualTo(MessageSource.Local); // v1 fallback
  }

  [Test]
  public async Task V1Envelope_WithVersionField_DeserializesCorrectlyAsync() {
    // v1 JSON with explicit version but no DispatchContext
    var json = """
    {
      "id": "019d0000-0000-7000-0000-000000000001",
      "v": 1,
      "p": { "Value": "explicit-v1" },
      "h": []
    }
    """;

    var envelope = JsonSerializer.Deserialize<MessageEnvelope<JsonElement>>(json, _jsonOptions);

    await Assert.That(envelope).IsNotNull();
    await Assert.That(envelope!.Version).IsEqualTo(1);
    await Assert.That(envelope.DispatchContext.Mode).IsEqualTo(DispatchModes.Outbox); // v1 fallback
  }

  // ========================================
  // DispatchContext round-trip (serialize → deserialize)
  // ========================================

  [Test]
  public async Task DispatchContext_RoundTrips_ThroughJsonSerializationAsync() {
    var original = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("round-trip"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Both, Source = MessageSource.Inbox }
    };

    var json = JsonSerializer.Serialize(original);
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<TestPayload>>(json);

    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.DispatchContext.Mode).IsEqualTo(DispatchModes.Both);
    await Assert.That(deserialized.DispatchContext.Source).IsEqualTo(MessageSource.Inbox);
    await Assert.That(deserialized.Version).IsEqualTo(1);
  }

  [Test]
  public async Task Version_RoundTrips_ThroughJsonSerializationAsync() {
    var original = new MessageEnvelope<TestPayload> {
      MessageId = MessageId.New(),
      Payload = new TestPayload("version-test"),
      Hops = [],
      Version = 1,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    var json = JsonSerializer.Serialize(original);
    var deserialized = JsonSerializer.Deserialize<MessageEnvelope<TestPayload>>(json);

    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.Version).IsEqualTo(1);
  }

  // ========================================
  // MessageDispatchContext record behavior
  // ========================================

  [Test]
  public async Task MessageDispatchContext_Equality_SameValues_AreEqualAsync() {
    var a = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local };
    var b = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local };

    await Assert.That(a).IsEqualTo(b);
  }

  [Test]
  public async Task MessageDispatchContext_Equality_DifferentValues_AreNotEqualAsync() {
    var a = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local };
    var b = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox };

    await Assert.That(a).IsNotEqualTo(b);
  }

  [Test]
  public async Task MessageDispatchContext_With_CreatesModifiedCopyAsync() {
    var original = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local };
    var modified = original with { Source = MessageSource.Inbox };

    await Assert.That(modified.Mode).IsEqualTo(DispatchModes.Local);
    await Assert.That(modified.Source).IsEqualTo(MessageSource.Inbox);
    await Assert.That(original.Source).IsEqualTo(MessageSource.Local); // original unchanged
  }

  // ========================================
  // EnvelopeMetadata DispatchContext persistence
  // ========================================

  [Test]
  public async Task EnvelopeMetadata_WithDispatchContext_SerializesAsync() {
    var metadata = new EnvelopeMetadata {
      MessageId = MessageId.New(),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Both, Source = MessageSource.Outbox }
    };

    var json = JsonSerializer.Serialize(metadata);
    var deserialized = JsonSerializer.Deserialize<EnvelopeMetadata>(json, _jsonOptions);

    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.DispatchContext).IsNotNull();
    await Assert.That(deserialized.DispatchContext!.Mode).IsEqualTo(DispatchModes.Both);
    await Assert.That(deserialized.DispatchContext.Source).IsEqualTo(MessageSource.Outbox);
  }

  [Test]
  public async Task EnvelopeMetadata_WithoutDispatchContext_DeserializesAsNullAsync() {
    // v1 metadata JSON — no "dc" field
    var json = """
    {
      "MessageId": "019d0000-0000-7000-0000-000000000001",
      "Hops": []
    }
    """;

    var deserialized = JsonSerializer.Deserialize<EnvelopeMetadata>(json, _jsonOptions);

    await Assert.That(deserialized).IsNotNull();
    await Assert.That(deserialized!.DispatchContext).IsNull(); // nullable for v1 compat
  }
}
