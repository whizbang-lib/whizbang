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

  /// <summary>
  /// Returns the generation policy for a message type based on [GenerateStreamId] attribute.
  /// Used by the Dispatcher to determine if a StreamId should be auto-generated.
  /// </summary>
  /// <param name="message">The message to check for generation policy</param>
  /// <returns>A tuple of (ShouldGenerate, OnlyIfEmpty) indicating the generation policy.
  /// ShouldGenerate=false means no auto-generation (default for events without [GenerateStreamId]).</returns>
  (bool ShouldGenerate, bool OnlyIfEmpty) GetGenerationPolicy(object message) => (false, false);

  /// <summary>
  /// Sets the stream ID on a message using the [StreamId]-marked property.
  /// Used by the Dispatcher to auto-generate StreamIds without requiring IHasStreamId.
  /// </summary>
  /// <param name="message">The message to set the StreamId on</param>
  /// <param name="streamId">The StreamId value to set</param>
  /// <returns>True if the StreamId was set, false if the message type is not recognized</returns>
  bool SetStreamId(object message, Guid streamId) => false;
}
