using Whizbang.Core.Generated;

namespace Whizbang.Core;

/// <summary>
/// Extracts stream IDs from messages for delivery receipts and routing.
/// Uses the unified [StreamId] attribute on events, commands, and perspective models.
/// Delegates to source-generated extractors for zero-reflection, AOT-compatible extraction.
/// </summary>
/// <docs>core-concepts/delivery-receipts</docs>
/// <tests>tests/Whizbang.Core.Tests/StreamIdExtractorTests.cs</tests>
public sealed class StreamIdExtractor : IStreamIdExtractor {

  /// <summary>
  /// Creates a new StreamIdExtractor.
  /// </summary>
  public StreamIdExtractor() {
  }

  /// <inheritdoc />
  public Guid? ExtractStreamId(object message, Type messageType) {
    if (message is null) {
      return null;
    }

    // Use unified [StreamId] extractors for all message types
    // The generator discovers [StreamId] on events, commands, and perspective DTOs
    if (message is IEvent @event) {
      return StreamIdExtractors.TryResolveAsGuid(@event);
    }

    if (message is ICommand command) {
      return StreamIdExtractors.TryResolveAsGuid(command);
    }

    // For other message types (e.g., perspective DTOs), try generic extraction
    return StreamIdExtractors.TryResolveAsGuid(message);
  }

  /// <inheritdoc />
  public (bool ShouldGenerate, bool OnlyIfEmpty) GetGenerationPolicy(object message) {
    return StreamIdExtractors.GetGenerationPolicy(message);
  }
}
