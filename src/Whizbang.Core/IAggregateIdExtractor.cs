namespace Whizbang.Core;

/// <summary>
/// Provides zero-reflection extraction of aggregate IDs from messages.
/// Implementations are source-generated per assembly containing [AggregateId] attributes.
/// </summary>
/// <remarks>
/// This interface enables PolicyContext to extract aggregate IDs without reflection
/// by using dependency injection to access the generated extractor implementation.
/// The source generator creates an implementation of this interface in the consumer
/// assembly that knows about all message types with [AggregateId] attributes.
/// </remarks>
public interface IAggregateIdExtractor {
  /// <summary>
  /// Extracts the aggregate ID from a message using compile-time type information.
  /// Zero reflection - uses source-generated type switches for optimal performance.
  /// </summary>
  /// <param name="message">The message instance</param>
  /// <param name="messageType">The runtime type of the message</param>
  /// <returns>The aggregate ID if found and marked with [AggregateId], otherwise null</returns>
  Guid? ExtractAggregateId(object message, Type messageType);
}
