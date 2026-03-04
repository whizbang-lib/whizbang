namespace Whizbang.Core;

/// <summary>
/// Extracts stream IDs from messages for delivery receipts and routing.
/// Uses [StreamId] attribute on both events and commands.
/// Uses source-generated extractors - zero reflection, AOT compatible.
/// </summary>
/// <docs>core-concepts/delivery-receipts</docs>
/// <tests>tests/Whizbang.Core.Tests/StreamIdExtractorTests.cs</tests>
public interface IStreamIdExtractor {
  /// <summary>
  /// Extracts the stream ID from a message.
  /// Uses the [StreamId] attribute to identify the stream property.
  /// </summary>
  /// <param name="message">The message instance</param>
  /// <param name="messageType">The runtime type of the message</param>
  /// <returns>The stream ID if found, otherwise null</returns>
  Guid? ExtractStreamId(object message, Type messageType);
}
