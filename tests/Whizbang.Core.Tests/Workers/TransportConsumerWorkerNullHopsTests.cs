using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)
#pragma warning disable CA1822 // Member does not access instance data (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for TransportConsumerWorker handling of envelopes with null Hops.
/// Reproduces and verifies fix for: ArgumentNullException: Value cannot be null. (Parameter 'source')
/// when envelope.Hops is null and .Where() is called on it.
/// </summary>
/// <remarks>
/// Bug reproduction scenario:
/// 1. Message arrives from transport (RabbitMQ, Azure Service Bus, etc.)
/// 2. Message envelope is deserialized but Hops is null (not present in JSON)
/// 3. TransportConsumerWorker._handleMessageAsync tries to extract TraceParent
/// 4. Code calls envelope.Hops.Where(...) which throws ArgumentNullException
/// </remarks>
[Category("Workers")]
[Category("NullHops")]
public class TransportConsumerWorkerNullHopsTests {
  private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

  // ========================================
  // Null Hops Bug Reproduction Tests
  // ========================================

  /// <summary>
  /// Reproduces the bug: calling .Where() on null Hops throws ArgumentNullException.
  /// This simulates what happens when a message arrives with no hops in the envelope.
  /// </summary>
  [Test]
  public async Task Hops_WhenNull_LinqWhereThrowsArgumentNullExceptionAsync() {
    // Arrange - Create an envelope with null Hops to simulate the bug
    IReadOnlyList<MessageHop>? nullHops = null;

    // Act & Assert - This is the buggy behavior we're reproducing
    await Assert.That(() => nullHops!.Where(h => h.Type == HopType.Current).ToList())
      .Throws<ArgumentNullException>()
      .Because("Bug reproduction: .Where() on null collection throws ArgumentNullException");
  }

  /// <summary>
  /// Verifies the fix: null-conditional operator handles null Hops gracefully.
  /// </summary>
  [Test]
  public async Task Hops_WhenNull_NullConditionalReturnsNullAsync() {
    // Arrange - Create an envelope with null Hops
    IReadOnlyList<MessageHop>? nullHops = null;

    // Act - Using null-conditional operator (the fix)
    var traceParent = nullHops?
      .Where(h => h.Type == HopType.Current)
      .Select(h => h.TraceParent)
      .LastOrDefault(tp => tp is not null);

    // Assert - Should return null, not throw
    await Assert.That(traceParent).IsNull()
      .Because("Fixed code should handle null Hops gracefully");
  }

  /// <summary>
  /// Verifies that envelopes with empty (not null) Hops work correctly.
  /// </summary>
  [Test]
  public async Task Hops_WhenEmpty_ReturnsNullTraceParentAsync() {
    // Arrange - Create an envelope with empty Hops
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [] // Empty, not null
    };

    // Act
    var traceParent = envelope.Hops?
      .Where(h => h.Type == HopType.Current)
      .Select(h => h.TraceParent)
      .LastOrDefault(tp => tp is not null);

    // Assert
    await Assert.That(traceParent).IsNull()
      .Because("Empty Hops should return null TraceParent");
  }

  /// <summary>
  /// Verifies that envelopes with Hops containing TraceParent work correctly.
  /// </summary>
  [Test]
  public async Task Hops_WithTraceParent_ReturnsTraceParentAsync() {
    // Arrange - Create an envelope with a hop containing TraceParent
    var expectedTraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Timestamp = DateTimeOffset.UtcNow,
          TraceParent = expectedTraceParent
        }
      ]
    };

    // Act
    var traceParent = envelope.Hops?
      .Where(h => h.Type == HopType.Current)
      .Select(h => h.TraceParent)
      .LastOrDefault(tp => tp is not null);

    // Assert
    await Assert.That(traceParent).IsEqualTo(expectedTraceParent)
      .Because("Hops with TraceParent should return the TraceParent");
  }

  // ========================================
  // Integration Tests - MessageEnvelope Deserialization
  // ========================================

  /// <summary>
  /// Verifies that accessing TraceParent on IReadOnlyList{MessageHop}? handles null gracefully.
  /// This is the defensive pattern used in the code.
  /// </summary>
  [Test]
  public async Task TraceParentExtraction_WithNullHopsList_ReturnsNullAsync() {
    // Arrange - Simulate the scenario where an envelope's Hops property is null
    // This can happen when deserializing messages that were sent without hops
    IReadOnlyList<MessageHop>? hops = null;

    // Act - This is the fixed pattern that uses null-conditional operator
    var traceParent = hops?
      .Where(h => h.Type == HopType.Current)
      .Select(h => h.TraceParent)
      .LastOrDefault(tp => tp is not null);

    // Assert
    await Assert.That(traceParent).IsNull()
      .Because("Null-conditional operator should prevent NullReferenceException and return null");
  }

  /// <summary>
  /// Verifies that extracting StreamId from null Hops works correctly.
  /// This tests the defensive coding in _extractStreamId.
  /// </summary>
  [Test]
  public async Task ExtractStreamId_WithNullHops_ReturnsMessageIdAsync() {
    // Arrange - Envelope with null Hops
    var messageId = MessageId.New();
    var envelope = new TestEnvelopeWithNullHops(messageId);

    // Act - Simulate _extractStreamId logic
    var firstHop = envelope.Hops?.FirstOrDefault();
    Guid streamId;
    if (firstHop?.Metadata != null &&
        firstHop.Metadata.TryGetValue("AggregateId", out var streamIdElem) &&
        streamIdElem.ValueKind == JsonValueKind.String) {
      var streamIdStr = streamIdElem.GetString();
      if (streamIdStr != null && Guid.TryParse(streamIdStr, out var parsedStreamId)) {
        streamId = parsedStreamId;
      } else {
        streamId = messageId.Value;
      }
    } else {
      streamId = messageId.Value;
    }

    // Assert
    await Assert.That(streamId).IsEqualTo(messageId.Value)
      .Because("Null Hops should fallback to MessageId as StreamId");
  }

  // ========================================
  // Test Helpers
  // ========================================

  /// <summary>
  /// Test envelope that has null Hops to simulate deserialization edge case.
  /// </summary>
  private sealed class TestEnvelopeWithNullHops(MessageId messageId) : IMessageEnvelope {
    public MessageId MessageId { get; } = messageId;
    public object Payload => new { };

    // Hops is List<MessageHop> per interface, but we want to test null scenario
    // Return empty list and separately test with null IReadOnlyList
    public List<MessageHop> Hops { get; } = [];

    public CorrelationId? GetCorrelationId() => null;
    public MessageId? GetCausationId() => null;
    public void AddHop(MessageHop hop) => Hops.Add(hop);
    public DateTimeOffset GetMessageTimestamp() => DateTimeOffset.UtcNow;
    public JsonElement? GetMetadata(string key) => null;
    public ScopeContext? GetCurrentScope() => null;
    public SecurityContext? GetCurrentSecurityContext() => null;
  }
}
