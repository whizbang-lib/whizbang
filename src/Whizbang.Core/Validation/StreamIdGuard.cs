using System.Runtime.CompilerServices;

namespace Whizbang.Core.Validation;

/// <summary>
/// Fail-fast validation guards for StreamId values at pipeline boundaries.
/// Throws <see cref="InvalidStreamIdException"/> when an event that MUST have a StreamId
/// is found with Guid.Empty, indicating a bug in the dispatch pipeline.
/// </summary>
/// <docs>fundamentals/events/stream-id#validation</docs>
/// <tests>tests/Whizbang.Core.Tests/Validation/StreamIdGuardTests.cs</tests>
public static class StreamIdGuard {
  /// <summary>
  /// Throws if the StreamId is Guid.Empty.
  /// Use for non-nullable Guid StreamIds (e.g., after _extractStreamId returns Guid).
  /// </summary>
  /// <param name="streamId">The StreamId value to validate.</param>
  /// <param name="messageId">The MessageId for diagnostic context.</param>
  /// <param name="context">The pipeline context (e.g., "Dispatcher.Outbox").</param>
  /// <param name="eventType">The event type name for diagnostic context.</param>
  /// <param name="caller">Auto-captured caller member name.</param>
  /// <param name="file">Auto-captured caller file path.</param>
  /// <param name="line">Auto-captured caller line number.</param>
  public static void ThrowIfEmpty(
      Guid streamId,
      Guid messageId,
      string context,
      string eventType = "",
      [CallerMemberName] string caller = "",
      [CallerFilePath] string file = "",
      [CallerLineNumber] int line = 0) {
    if (streamId == Guid.Empty) {
      throw new InvalidStreamIdException(
          $"StreamId is Guid.Empty for {eventType} (MessageId={messageId}) at {context}. " +
          "Events with [StreamId] must have a non-empty StreamId. " +
          "Either apply [GenerateStreamId] to auto-generate, or provide a StreamId before dispatch.") {
        StreamId = streamId,
        MessageId = messageId,
        Context = context,
        CallerMemberName = caller,
        CallerFilePath = file,
        CallerLineNumber = line
      };
    }
  }

  /// <summary>
  /// Throws if the StreamId is non-null AND Guid.Empty.
  /// Use for nullable Guid? StreamIds (e.g., OutboxMessage.StreamId, InboxMessage.StreamId).
  /// A null StreamId is valid (means the event has no stream concept), but Guid.Empty is a bug.
  /// </summary>
  /// <param name="streamId">The nullable StreamId value to validate.</param>
  /// <param name="messageId">The MessageId for diagnostic context.</param>
  /// <param name="context">The pipeline context (e.g., "WorkCoordinator.QueueOutbox").</param>
  /// <param name="eventType">The event type name for diagnostic context.</param>
  /// <param name="caller">Auto-captured caller member name.</param>
  /// <param name="file">Auto-captured caller file path.</param>
  /// <param name="line">Auto-captured caller line number.</param>
  public static void ThrowIfNonNullEmpty(
      Guid? streamId,
      Guid messageId,
      string context,
      string eventType = "",
      [CallerMemberName] string caller = "",
      [CallerFilePath] string file = "",
      [CallerLineNumber] int line = 0) {
    if (streamId.HasValue && streamId.Value == Guid.Empty) {
      throw new InvalidStreamIdException(
          $"StreamId is Guid.Empty (non-null) for {eventType} (MessageId={messageId}) at {context}. " +
          "A null StreamId is valid (no stream concept), but Guid.Empty indicates a bug. " +
          "Either apply [GenerateStreamId] to auto-generate, or provide a StreamId before dispatch.") {
        StreamId = streamId.Value,
        MessageId = messageId,
        Context = context,
        CallerMemberName = caller,
        CallerFilePath = file,
        CallerLineNumber = line
      };
    }
  }
}
