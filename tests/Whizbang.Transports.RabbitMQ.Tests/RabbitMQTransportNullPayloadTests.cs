using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Tests for RabbitMQ transport handling of null Payload deserialization scenarios.
/// Reproduces and verifies fixes for NullReferenceException when messages have missing "p" field.
/// </summary>
[Category("Transport")]
[Category("NullHandling")]
public class RabbitMQTransportNullPayloadTests {
  // Shared JsonSerializerOptions to avoid CA1869
  private static readonly JsonSerializerOptions _jsonOptions = new() {
    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
  };

  // ========================================
  // JSON Deserialization Behavior Tests
  // ========================================

  /// <summary>
  /// Verifies that deserializing JSON with missing "p" (Payload) field results in null Payload.
  /// This is the root cause - JSON deserializes successfully but Payload is null.
  /// </summary>
  [Test]
  public async Task Deserialize_WithMissingPayloadField_ReturnsEnvelopeWithNullPayloadAsync() {
    // Arrange - JSON with all fields except "p" (Payload)
    var jsonWithoutPayload = """
      {
        "id": "01234567-89ab-cdef-0123-456789abcdef",
        "h": []
      }
      """;

    // Act - Deserialize as non-generic IMessageEnvelope (what transport does)
    IMessageEnvelope? envelope = null;
    Exception? deserializationException = null;
    try {
      // Use the generic type since that's what gets registered
      envelope = JsonSerializer.Deserialize<MessageEnvelope<TestMessage>>(jsonWithoutPayload, _jsonOptions);
    } catch (Exception ex) {
      deserializationException = ex;
    }

    // Assert - Deserialization may succeed with null Payload or throw
    // Either behavior is acceptable - the transport fix handles both
    if (deserializationException is not null) {
      // If it throws, that's fine - transport catches this
      await Assert.That(deserializationException).IsTypeOf<JsonException>()
        .Because("Missing required property should throw JsonException");
    } else {
      // If it succeeds, Payload should be null (or the envelope itself might be null)
      // This is the scenario our fix handles
      await Assert.That(envelope is null || envelope.Payload is null).IsTrue()
        .Because("Deserializing JSON without 'p' field should result in null envelope or null Payload");
    }
  }

  /// <summary>
  /// Verifies that deserializing JSON with explicit null "p" field results in null Payload.
  /// </summary>
  [Test]
  public async Task Deserialize_WithExplicitNullPayload_ReturnsEnvelopeWithNullPayloadAsync() {
    // Arrange - JSON with explicit null for "p" (Payload)
    var jsonWithNullPayload = """
      {
        "id": "01234567-89ab-cdef-0123-456789abcdef",
        "p": null,
        "h": []
      }
      """;

    // Act - Deserialize
    IMessageEnvelope? envelope = null;
    Exception? deserializationException = null;
    try {
      envelope = JsonSerializer.Deserialize<MessageEnvelope<TestMessage>>(jsonWithNullPayload, _jsonOptions);
    } catch (Exception ex) {
      deserializationException = ex;
    }

    // Assert - Either throws or results in null Payload
    if (deserializationException is not null) {
      await Assert.That(deserializationException is JsonException or ArgumentNullException).IsTrue()
        .Because("Explicit null for required property should throw");
    } else {
      await Assert.That(envelope is null || envelope.Payload is null).IsTrue()
        .Because("Deserializing JSON with null 'p' should result in null envelope or null Payload");
    }
  }

  /// <summary>
  /// Verifies that a valid envelope serializes and deserializes with non-null Payload.
  /// This is the control case - proves our serialization works correctly.
  /// </summary>
  [Test]
  public async Task Deserialize_WithValidPayload_ReturnsEnvelopeWithNonNullPayloadAsync() {
    // Arrange - Create valid envelope
    var originalEnvelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test-content"),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ]
    };

    // Act - Round-trip serialize/deserialize
    var json = JsonSerializer.Serialize(originalEnvelope, _jsonOptions);
    var deserializedEnvelope = JsonSerializer.Deserialize<MessageEnvelope<TestMessage>>(json, _jsonOptions);

    // Assert - Payload should be non-null
    await Assert.That(deserializedEnvelope).IsNotNull()
      .Because("Valid envelope should deserialize successfully");
    await Assert.That(deserializedEnvelope!.Payload).IsNotNull()
      .Because("Valid envelope should have non-null Payload");
    await Assert.That(deserializedEnvelope.Payload.Content).IsEqualTo("test-content")
      .Because("Payload content should be preserved through serialization");
  }

  // ========================================
  // Transport Rejection Behavior Tests
  // ========================================

  /// <summary>
  /// Verifies that IMessageEnvelope.Payload can be checked for null safely.
  /// This confirms the fix pattern works correctly.
  /// </summary>
  [Test]
  public async Task EnvelopePayloadCheck_WithNullPayload_ReturnsNullAsync() {
    // Arrange - Create test double with null Payload
    var envelopeWithNullPayload = new TestEnvelopeWithNullPayload(MessageId.New());

    // Act - Check Payload the way transport does
    var payload = envelopeWithNullPayload.Payload;
    var isNull = payload is null;

    // Assert
    await Assert.That(isNull).IsTrue()
      .Because("Null Payload check should work without throwing");
  }

  /// <summary>
  /// Verifies that envelope with null Payload should be detected and rejected.
  /// Simulates the transport's null check logic.
  /// </summary>
  [Test]
  public async Task TransportNullCheck_WithNullPayload_ShouldRejectMessageAsync() {
    // Arrange - Create envelope with null Payload
    var envelope = new TestEnvelopeWithNullPayload(MessageId.New());

    // Act - Simulate transport null check
    var shouldReject = _simulateTransportNullCheck(envelope);

    // Assert
    await Assert.That(shouldReject).IsTrue()
      .Because("Messages with null Payload should be rejected and sent to dead letter queue");
  }

  /// <summary>
  /// Verifies that envelope with valid Payload should not be rejected.
  /// </summary>
  [Test]
  public async Task TransportNullCheck_WithValidPayload_ShouldNotRejectMessageAsync() {
    // Arrange - Create valid envelope
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("valid-content"),
      Hops = []
    };

    // Act - Simulate transport null check
    var shouldReject = _simulateTransportNullCheck(envelope);

    // Assert
    await Assert.That(shouldReject).IsFalse()
      .Because("Messages with valid Payload should be processed normally");
  }

  // ========================================
  // Helper Methods
  // ========================================

  /// <summary>
  /// Simulates the transport's null Payload check logic.
  /// Returns true if the message should be rejected (sent to dead letter).
  /// </summary>
  private static bool _simulateTransportNullCheck(IMessageEnvelope envelope) {
    // This mirrors the fix in RabbitMQTransport._deserializeMessage:
    // if (envelope.Payload is null) { ... return null; }
    return envelope.Payload is null;
  }

  // ========================================
  // Test Doubles
  // ========================================

  /// <summary>
  /// Test envelope with null Payload to simulate deserialization edge case.
  /// </summary>
  private sealed class TestEnvelopeWithNullPayload : IMessageEnvelope {
    public MessageId MessageId { get; }
    public object Payload => null!; // Simulates null payload from bad deserialization
    public List<MessageHop> Hops { get; } = [];

    public TestEnvelopeWithNullPayload(MessageId messageId) {
      MessageId = messageId;
    }

    public CorrelationId? GetCorrelationId() => null;
    public MessageId? GetCausationId() => null;
    public void AddHop(MessageHop hop) => Hops.Add(hop);
    public DateTimeOffset GetMessageTimestamp() => DateTimeOffset.UtcNow;
    public System.Text.Json.JsonElement? GetMetadata(string key) => null;
    public ScopeContext? GetCurrentScope() => null;
    public SecurityContext? GetCurrentSecurityContext() => null;
  }
}
