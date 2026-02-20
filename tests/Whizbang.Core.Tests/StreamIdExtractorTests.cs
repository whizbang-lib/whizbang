using Rocks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for StreamIdExtractor.
/// Verifies [StreamKey] (events) and [AggregateId] (commands) extraction priority.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/StreamIdExtractorTests.cs</tests>
[Category("StreamId")]
[Category("DeliveryReceipt")]
public class StreamIdExtractorTests {

  // ========================================
  // Test Events and Commands
  // ========================================

  /// <summary>Event with [StreamKey] attribute</summary>
  public record TestEventWithStreamKey([property: StreamKey] Guid OrderId, string Name) : IEvent;

  /// <summary>Event with both [StreamKey] and [AggregateId] (StreamKey should be preferred)</summary>
  public record TestEventWithBothAttributes(
    [property: StreamKey] Guid StreamId,
    [property: AggregateId] Guid AggregateId,
    string Name
  ) : IEvent;

  /// <summary>Event with only [AggregateId] (fallback)</summary>
#pragma warning disable WHIZ009 // Intentionally missing [StreamKey] for testing fallback behavior
  public record TestEventWithAggregateIdOnly([property: AggregateId] Guid AggregateId, string Name) : IEvent;
#pragma warning restore WHIZ009

  /// <summary>Event with neither attribute</summary>
#pragma warning disable WHIZ009 // Intentionally missing [StreamKey] for testing null return behavior
  public record TestEventWithNoAttributes(string Name) : IEvent;
#pragma warning restore WHIZ009

  /// <summary>Command with [AggregateId] attribute</summary>
  public record TestCommandWithAggregateId([property: AggregateId] Guid OrderId, string Data) : ICommand;

  /// <summary>Command without [AggregateId] attribute</summary>
  public record TestCommandWithNoAggregateId(string Data) : ICommand;

  /// <summary>Regular message (not IEvent or ICommand) with [AggregateId]</summary>
  public record TestMessageWithAggregateId([property: AggregateId] Guid Id, string Data) : IMessage;

  // ========================================
  // IEvent Tests
  // ========================================

  [Test]
  public async Task ExtractStreamId_EventWithStreamKey_ReturnsStreamKeyValueAsync() {
    // Arrange
    var expectedId = Guid.NewGuid();
    var @event = new TestEventWithStreamKey(expectedId, "Test");
    var extractor = new TestStreamIdExtractor();

    // Act
    var result = extractor.ExtractStreamId(@event, @event.GetType());

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo(expectedId);
  }

  [Test]
  public async Task ExtractStreamId_EventWithStreamKeyAndAggregateId_PrefersStreamKeyAsync() {
    // Arrange
    var streamKeyId = Guid.NewGuid();
    var aggregateId = Guid.NewGuid();
    var @event = new TestEventWithBothAttributes(streamKeyId, aggregateId, "Test");
    var extractor = new TestStreamIdExtractor();

    // Act
    var result = extractor.ExtractStreamId(@event, @event.GetType());

    // Assert - Should use [StreamKey], not [AggregateId]
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo(streamKeyId);
    await Assert.That(result.Value).IsNotEqualTo(aggregateId);
  }

  [Test]
  public async Task ExtractStreamId_EventWithoutStreamKey_FallsBackToAggregateIdAsync() {
    // Arrange
    var expectedId = Guid.NewGuid();
    var @event = new TestEventWithAggregateIdOnly(expectedId, "Test");

    // Create mock for IAggregateIdExtractor
    var mockExtractor = new AggregateIdExtractorMock();
    mockExtractor.SetupExtractAggregateId(@event, @event.GetType(), expectedId);

    var extractor = new TestStreamIdExtractor(mockExtractor);

    // Act
    var result = extractor.ExtractStreamId(@event, @event.GetType());

    // Assert - Should fall back to [AggregateId]
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo(expectedId);
  }

  [Test]
  public async Task ExtractStreamId_EventWithNeither_ReturnsNullAsync() {
    // Arrange
    var @event = new TestEventWithNoAttributes("Test");

    // Mock returns null (no [AggregateId] found)
    var mockExtractor = new AggregateIdExtractorMock();
    mockExtractor.SetupExtractAggregateId(@event, @event.GetType(), null);

    var extractor = new TestStreamIdExtractor(mockExtractor);

    // Act
    var result = extractor.ExtractStreamId(@event, @event.GetType());

    // Assert
    await Assert.That(result).IsNull();
  }

  // ========================================
  // ICommand Tests
  // ========================================

  [Test]
  public async Task ExtractStreamId_CommandWithAggregateId_ReturnsAggregateIdAsync() {
    // Arrange
    var expectedId = Guid.NewGuid();
    var command = new TestCommandWithAggregateId(expectedId, "Test");

    var mockExtractor = new AggregateIdExtractorMock();
    mockExtractor.SetupExtractAggregateId(command, command.GetType(), expectedId);

    var extractor = new TestStreamIdExtractor(mockExtractor);

    // Act
    var result = extractor.ExtractStreamId(command, command.GetType());

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo(expectedId);
  }

  [Test]
  public async Task ExtractStreamId_CommandWithoutAggregateId_ReturnsNullAsync() {
    // Arrange
    var command = new TestCommandWithNoAggregateId("Test");

    var mockExtractor = new AggregateIdExtractorMock();
    mockExtractor.SetupExtractAggregateId(command, command.GetType(), null);

    var extractor = new TestStreamIdExtractor(mockExtractor);

    // Act
    var result = extractor.ExtractStreamId(command, command.GetType());

    // Assert
    await Assert.That(result).IsNull();
  }

  // ========================================
  // Edge Cases
  // ========================================

  [Test]
  public async Task ExtractStreamId_NullMessage_ReturnsNullAsync() {
    // Arrange
    var extractor = new TestStreamIdExtractor();

    // Act
    var result = extractor.ExtractStreamId(null!, typeof(object));

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ExtractStreamId_NonEventNonCommand_UsesAggregateIdAsync() {
    // Arrange
    var expectedId = Guid.NewGuid();
    var message = new TestMessageWithAggregateId(expectedId, "Test");

    var mockExtractor = new AggregateIdExtractorMock();
    mockExtractor.SetupExtractAggregateId(message, message.GetType(), expectedId);

    var extractor = new TestStreamIdExtractor(mockExtractor);

    // Act
    var result = extractor.ExtractStreamId(message, message.GetType());

    // Assert - Should use [AggregateId] since not an IEvent with [StreamKey]
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo(expectedId);
  }

  [Test]
  public async Task ExtractStreamId_NoExtractorProvided_UsesStreamKeyOnlyForEventsAsync() {
    // Arrange
    var expectedId = Guid.NewGuid();
    var @event = new TestEventWithStreamKey(expectedId, "Test");

    // No IAggregateIdExtractor provided
    var extractor = new TestStreamIdExtractor(null);

    // Act
    var result = extractor.ExtractStreamId(@event, @event.GetType());

    // Assert - Should still work via [StreamKey]
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo(expectedId);
  }

  [Test]
  public async Task ExtractStreamId_NoExtractorAndNoStreamKey_ReturnsNullAsync() {
    // Arrange
    var command = new TestCommandWithAggregateId(Guid.NewGuid(), "Test");

    // No IAggregateIdExtractor provided, and command has no [StreamKey]
    var extractor = new TestStreamIdExtractor(null);

    // Act
    var result = extractor.ExtractStreamId(command, command.GetType());

    // Assert - Should return null since no fallback available
    await Assert.That(result).IsNull();
  }

  // ========================================
  // Test Support Classes
  // ========================================

  /// <summary>
  /// Test-specific StreamIdExtractor that uses the test project's generated extractors.
  /// This is needed because the test project generates its own StreamKeyExtractors
  /// in Whizbang.Core.Tests.Generated, separate from Whizbang.Core.Generated.
  /// </summary>
  private sealed class TestStreamIdExtractor : IStreamIdExtractor {
    private readonly IAggregateIdExtractor? _aggregateIdExtractor;

    public TestStreamIdExtractor(IAggregateIdExtractor? aggregateIdExtractor = null) {
      _aggregateIdExtractor = aggregateIdExtractor;
    }

    public Guid? ExtractStreamId(object message, Type messageType) {
      if (message is null) {
        return null;
      }

      // For IEvent: Try [StreamKey] first using the test project's generated extractors
      if (message is IEvent @event) {
        var streamId = StreamKeyExtractors.TryResolveAsGuid(@event);
        if (streamId.HasValue) {
          return streamId.Value;
        }
      }

      // Fall back to [AggregateId]
      return _aggregateIdExtractor?.ExtractAggregateId(message, messageType);
    }
  }

  /// <summary>
  /// Simple mock for IAggregateIdExtractor.
  /// </summary>
  private sealed class AggregateIdExtractorMock : IAggregateIdExtractor {
    private readonly Dictionary<(object, Type), Guid?> _results = new();

    public void SetupExtractAggregateId(object message, Type messageType, Guid? result) {
      _results[(message, messageType)] = result;
    }

    public Guid? ExtractAggregateId(object message, Type messageType) {
      return _results.TryGetValue((message, messageType), out var result) ? result : null;
    }
  }
}
