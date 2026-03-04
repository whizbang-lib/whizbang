using Rocks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for StreamIdExtractor.
/// Verifies [StreamId] (events) and [StreamId] (commands) extraction priority.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/StreamIdExtractorTests.cs</tests>
[Category("StreamId")]
[Category("DeliveryReceipt")]
public class StreamIdExtractorTests {

  // ========================================
  // Test Events and Commands
  // ========================================

  /// <summary>Event with [StreamId] attribute</summary>
  public record TestEventWithStreamId([property: StreamId] Guid OrderId, string Name) : IEvent;

  /// <summary>Event with [StreamId] attribute on StreamId property</summary>
  public record TestEventWithStreamIdMark(
    [property: StreamId] Guid StreamId,
    string Name
  ) : IEvent;

  /// <summary>Event with only [StreamId] (fallback)</summary>
#pragma warning disable WHIZ009 // Intentionally missing [StreamId] for testing fallback behavior
  public record TestEventWithStreamIdOnly([property: StreamId] Guid StreamId, string Name) : IEvent;
#pragma warning restore WHIZ009

  /// <summary>Event with neither attribute</summary>
#pragma warning disable WHIZ009 // Intentionally missing [StreamId] for testing null return behavior
  public record TestEventWithNoAttributes(string Name) : IEvent;
#pragma warning restore WHIZ009

  /// <summary>Command with [StreamId] attribute</summary>
  public record TestCommandWithStreamId([property: StreamId] Guid OrderId, string Data) : ICommand;

  /// <summary>Command without [StreamId] attribute</summary>
  public record TestCommandWithNoStreamId(string Data) : ICommand;

  /// <summary>Regular message (not IEvent or ICommand) with [StreamId]</summary>
  public record TestMessageWithStreamId([property: StreamId] Guid Id, string Data) : IMessage;

  // ========================================
  // IEvent Tests
  // ========================================

  [Test]
  public async Task ExtractStreamId_EventWithStreamId_ReturnsStreamIdValueAsync() {
    // Arrange
    var expectedId = Guid.NewGuid();
    var @event = new TestEventWithStreamId(expectedId, "Test");
    var extractor = new TestStreamIdExtractor();

    // Act
    var result = extractor.ExtractStreamId(@event, @event.GetType());

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo(expectedId);
  }

  [Test]
  public async Task ExtractStreamId_EventWithStreamIdAttribute_ExtractsStreamIdAsync() {
    // Arrange
    var expectedId = Guid.NewGuid();
    var @event = new TestEventWithStreamIdMark(expectedId, "Test");
    var extractor = new TestStreamIdExtractor();

    // Act
    var result = extractor.ExtractStreamId(@event, @event.GetType());

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo(expectedId);
  }

  [Test]
  public async Task ExtractStreamId_EventWithoutStreamId_FallsBackToStreamIdAsync() {
    // Arrange
    var expectedId = Guid.NewGuid();
    var @event = new TestEventWithStreamIdOnly(expectedId, "Test");

    // Create mock for IStreamIdExtractor
    var mockExtractor = new StreamIdExtractorMock();
    mockExtractor.SetupExtractStreamId(@event, @event.GetType(), expectedId);

    var extractor = new TestStreamIdExtractor(mockExtractor);

    // Act
    var result = extractor.ExtractStreamId(@event, @event.GetType());

    // Assert - Should fall back to [StreamId]
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo(expectedId);
  }

  [Test]
  public async Task ExtractStreamId_EventWithNeither_ReturnsNullAsync() {
    // Arrange
    var @event = new TestEventWithNoAttributes("Test");

    // Mock returns null (no [StreamId] found)
    var mockExtractor = new StreamIdExtractorMock();
    mockExtractor.SetupExtractStreamId(@event, @event.GetType(), null);

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
  public async Task ExtractStreamId_CommandWithStreamId_ReturnsStreamIdAsync() {
    // Arrange
    var expectedId = Guid.NewGuid();
    var command = new TestCommandWithStreamId(expectedId, "Test");

    var mockExtractor = new StreamIdExtractorMock();
    mockExtractor.SetupExtractStreamId(command, command.GetType(), expectedId);

    var extractor = new TestStreamIdExtractor(mockExtractor);

    // Act
    var result = extractor.ExtractStreamId(command, command.GetType());

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo(expectedId);
  }

  [Test]
  public async Task ExtractStreamId_CommandWithoutStreamId_ReturnsNullAsync() {
    // Arrange
    var command = new TestCommandWithNoStreamId("Test");

    var mockExtractor = new StreamIdExtractorMock();
    mockExtractor.SetupExtractStreamId(command, command.GetType(), null);

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
  public async Task ExtractStreamId_NonEventNonCommand_UsesStreamIdAsync() {
    // Arrange
    var expectedId = Guid.NewGuid();
    var message = new TestMessageWithStreamId(expectedId, "Test");

    var mockExtractor = new StreamIdExtractorMock();
    mockExtractor.SetupExtractStreamId(message, message.GetType(), expectedId);

    var extractor = new TestStreamIdExtractor(mockExtractor);

    // Act
    var result = extractor.ExtractStreamId(message, message.GetType());

    // Assert - Should use [StreamId] since not an IEvent with [StreamId]
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo(expectedId);
  }

  [Test]
  public async Task ExtractStreamId_NoExtractorProvided_UsesStreamIdOnlyForEventsAsync() {
    // Arrange
    var expectedId = Guid.NewGuid();
    var @event = new TestEventWithStreamId(expectedId, "Test");

    // No IStreamIdExtractor provided
    var extractor = new TestStreamIdExtractor(null);

    // Act
    var result = extractor.ExtractStreamId(@event, @event.GetType());

    // Assert - Should still work via [StreamId]
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Value).IsEqualTo(expectedId);
  }

  [Test]
  public async Task ExtractStreamId_NoExtractorAndNoStreamId_ReturnsNullAsync() {
    // Arrange
    var command = new TestCommandWithStreamId(Guid.NewGuid(), "Test");

    // No IStreamIdExtractor provided, and command has no [StreamId]
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
  /// This is needed because the test project generates its own StreamIdExtractors
  /// in Whizbang.Core.Tests.Generated, separate from Whizbang.Core.Generated.
  /// </summary>
  private sealed class TestStreamIdExtractor : IStreamIdExtractor {
    private readonly IStreamIdExtractor? _aggregateIdExtractor;

    public TestStreamIdExtractor(IStreamIdExtractor? aggregateIdExtractor = null) {
      _aggregateIdExtractor = aggregateIdExtractor;
    }

    public Guid? ExtractStreamId(object message, Type messageType) {
      if (message is null) {
        return null;
      }

      // For IEvent: Try [StreamId] first using the test project's generated extractors
      if (message is IEvent @event) {
        var streamId = StreamIdExtractors.TryResolveAsGuid(@event);
        if (streamId.HasValue) {
          return streamId.Value;
        }
      }

      // Fall back to [StreamId]
      return _aggregateIdExtractor?.ExtractStreamId(message, messageType);
    }
  }

  /// <summary>
  /// Simple mock for IStreamIdExtractor.
  /// </summary>
  private sealed class StreamIdExtractorMock : IStreamIdExtractor {
    private readonly Dictionary<(object, Type), Guid?> _results = new();

    public void SetupExtractStreamId(object message, Type messageType, Guid? result) {
      _results[(message, messageType)] = result;
    }

    public Guid? ExtractStreamId(object message, Type messageType) {
      return _results.TryGetValue((message, messageType), out var result) ? result : null;
    }
  }
}
