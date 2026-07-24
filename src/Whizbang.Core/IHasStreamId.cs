namespace Whizbang.Core;

/// <summary>
/// Interface for messages that have a settable StreamId.
/// When a message implements this interface and its StreamId is Guid.Empty,
/// Whizbang will automatically generate a new StreamId using TrackedGuid.NewMedo().
/// This prevents events from being stored with empty StreamIds.
/// </summary>
/// <docs>fundamentals/events/stream-id</docs>
public interface IHasStreamId {
  /// <summary>
  /// The stream identifier for this message.
  /// If empty when the message is dispatched, a new ID will be generated automatically.
  /// </summary>
  Guid StreamId { get; set; }
}
