using Whizbang.Core.Generated;

namespace Whizbang.Core;

/// <summary>
/// Extracts stream IDs from IEvent and ICommand messages.
/// Uses source-generated extractors for zero-reflection, AOT-compatible extraction.
/// </summary>
/// <remarks>
/// Extraction priority:
/// - IEvent: [StreamKey] → [AggregateId] → null
/// - ICommand: [AggregateId] → null
/// - Other messages: [AggregateId] → null
/// </remarks>
/// <docs>core-concepts/delivery-receipts</docs>
/// <tests>tests/Whizbang.Core.Tests/StreamIdExtractorTests.cs</tests>
public sealed class StreamIdExtractor : IStreamIdExtractor {
  private readonly IAggregateIdExtractor? _aggregateIdExtractor;

  /// <summary>
  /// Creates a new StreamIdExtractor with optional aggregate ID extractor for fallback.
  /// </summary>
  /// <param name="aggregateIdExtractor">Optional extractor for [AggregateId] attributes</param>
  public StreamIdExtractor(IAggregateIdExtractor? aggregateIdExtractor = null) {
    _aggregateIdExtractor = aggregateIdExtractor;
  }

  /// <inheritdoc />
  public Guid? ExtractStreamId(object message, Type messageType) {
    if (message is null) {
      return null;
    }

    // For IEvent: Try [StreamKey] first (event stream semantics)
    if (message is IEvent @event) {
      var streamId = StreamKeyExtractors.TryResolveAsGuid(@event);
      if (streamId.HasValue) {
        return streamId.Value;
      }
    }

    // For ICommand and all messages: Fall back to [AggregateId]
    return _aggregateIdExtractor?.ExtractAggregateId(message, messageType);
  }
}
