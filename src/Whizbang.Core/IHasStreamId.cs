namespace Whizbang.Core;

/// <summary>
/// Interface for messages that have a settable StreamId.
/// When a message implements this interface and its StreamId is Guid.Empty,
/// Whizbang will automatically generate a new StreamId using TrackedGuid.NewMedo().
/// This prevents events from being stored with empty StreamIds.
/// </summary>
/// <docs>fundamentals/events/stream-id</docs>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/StreamIdCascadeFromLocalInvokeTests.cs:LocalInvokeAsync_CommandWithGenerateStreamId_CascadedEventGetsStreamIdAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCoverageSweepOutboxTests.cs:PublishToOutbox_SourceStreamId_PropagatesToIHasStreamIdEventAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCoverageSweepOutboxTests.cs:CascadeMessageAsync_EventWithIHasStreamId_InheritsStreamIdFromSourceAsync</tests>
public interface IHasStreamId {
  /// <summary>
  /// The stream identifier for this message.
  /// If empty when the message is dispatched, a new ID will be generated automatically.
  /// </summary>
  Guid StreamId { get; set; }
}
