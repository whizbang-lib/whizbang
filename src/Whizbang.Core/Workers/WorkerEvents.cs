namespace Whizbang.Core.Workers;

/// <summary>
/// Callback invoked when work processing transitions from idle to active state.
/// Fires when work appears after consecutive empty polls.
/// </summary>
/// <docs>operations/workers/processing-hooks</docs>
public delegate void WorkProcessingStartedHandler();

/// <summary>
/// Callback invoked when work processing transitions from active to idle state.
/// Fires after a worker finishes a batch and finds no more pending work.
/// Useful for integration tests to wait for event processing completion.
/// </summary>
/// <docs>operations/workers/processing-hooks</docs>
public delegate void WorkProcessingIdleHandler();

/// <summary>
/// Callback invoked after a message is successfully published to transport.
/// Wired by <see cref="OutboxPublishWorker.OnOutboxMessagePublished"/>.
/// </summary>
/// <docs>operations/workers/outbox-publish-worker#processing-hooks</docs>
public delegate void OutboxMessagePublishedHandler(OutboxMessagePublishedEvent e);

/// <summary>
/// Event data for <see cref="OutboxMessagePublishedHandler"/>.
/// Carries all details about a successfully published outbox message.
/// </summary>
/// <docs>operations/workers/outbox-publish-worker#processing-hooks</docs>
public sealed record OutboxMessagePublishedEvent {
  /// <summary>The unique ID of the published message.</summary>
  public required Guid MessageId { get; init; }

  /// <summary>The transport destination (topic/queue name), or null for local-only.</summary>
  public string? Destination { get; init; }
}

/// <summary>
/// Callback invoked after a perspective successfully processes events for a stream.
/// Wired by <see cref="PerspectiveWorker.OnPerspectiveEventProcessed"/>.
/// </summary>
/// <docs>operations/workers/perspective-worker#processing-hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathReplayTests.cs:Worker_RewindWithReplayReader_PrependsPendingReplayEventsOnceAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathReplayTests.cs:Worker_RewindWithoutReplayReader_FallsBackToTriggerEnvelopeLookupAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathChannelTests.cs:Worker_NormalPathWithoutCoordinator_FiresPostLifecycleFallbackAndDetachedStagesAsync</tests>
public delegate void PerspectiveEventProcessedHandler(PerspectiveEventProcessedEvent e);

/// <summary>
/// Event data for <see cref="PerspectiveEventProcessedHandler"/>.
/// Carries all details about a perspective that finished processing events.
/// </summary>
/// <docs>operations/workers/perspective-worker#processing-hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathReplayTests.cs:Worker_RewindWithReplayReader_PrependsPendingReplayEventsOnceAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathReplayTests.cs:Worker_RewindWithoutReplayReader_FallsBackToTriggerEnvelopeLookupAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/PerspectiveWorkerDeepPathChannelTests.cs:Worker_NormalPathWithoutCoordinator_FiresPostLifecycleFallbackAndDetachedStagesAsync</tests>
public sealed record PerspectiveEventProcessedEvent {
  /// <summary>The name of the perspective that processed events.</summary>
  public required string PerspectiveName { get; init; }

  /// <summary>The stream ID that was processed.</summary>
  public required Guid StreamId { get; init; }

  /// <summary>The number of events processed in this batch.</summary>
  public required int EventCount { get; init; }
}
