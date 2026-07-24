using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Runs perspective event replay for a specific stream.
/// Loads events from event store, applies them to perspectives, and tracks checkpoint progress.
/// Generated code implements this interface for each discovered perspective.
/// </summary>
public interface IPerspectiveRunner {
  /// <summary>
  /// Gets the CLR type of the perspective this runner processes.
  /// Used by lifecycle stages to identify which perspective is processing.
  /// </summary>
  Type PerspectiveType { get; }

  /// <summary>
  /// Processes perspective checkpoint for a stream using unit-of-work pattern.
  /// Loads events from event store, applies them in UUID7 order, and saves checkpoint.
  /// Supports partial success - saves checkpoint at last successful event before failure.
  /// </summary>
  /// <param name="streamId">Stream ID to process</param>
  /// <param name="perspectiveName">Name of the perspective to run</param>
  /// <param name="lastProcessedEventId">Last event ID processed (null = start from beginning)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Perspective checkpoint completion with processing status and last event ID</returns>
  Task<PerspectiveCursorCompletion> RunAsync(
    Guid streamId,
    string perspectiveName,
    Guid? lastProcessedEventId,
    CancellationToken cancellationToken = default
  );

  /// <summary>
  /// Processes perspective checkpoint using pre-fetched events (drain mode optimization).
  /// Same as RunAsync but skips ReadPolymorphicAsync — uses supplied events directly.
  /// Eliminates redundant DB reads when events are already fetched by get_stream_events.
  /// </summary>
  /// <param name="streamId">Stream ID to process</param>
  /// <param name="perspectiveName">Name of the perspective to run</param>
  /// <param name="lastProcessedEventId">Last event ID processed (null = start from beginning)</param>
  /// <param name="events">Pre-fetched events to apply (already deserialized, in UUID7 order)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Perspective checkpoint completion with processing status and last event ID</returns>
  Task<PerspectiveCursorCompletion> RunWithEventsAsync(
    Guid streamId,
    string perspectiveName,
    Guid? lastProcessedEventId,
    IReadOnlyList<MessageEnvelope<IEvent>> events,
    CancellationToken cancellationToken = default
  ) => Task.FromResult(new PerspectiveCursorCompletion {
    StreamId = streamId,
    PerspectiveName = perspectiveName,
    LastEventId = lastProcessedEventId ?? Guid.Empty,
    Status = PerspectiveProcessingStatus.None
  });

  /// <summary>
  /// Replays a physical stream during a perspective REBUILD, honouring event upcasters that
  /// re-key an event's <c>[StreamId]</c>. Reads the physical stream's events (upcasted), partitions
  /// them by their post-upcast target stream id, and applies each partition onto its own row. The
  /// common case — no upcaster re-keys — yields a single partition equal to <paramref name="physicalStreamId"/>,
  /// so behaviour is identical to a single <see cref="RunAsync"/> call.
  ///
  /// <para>Returns one <see cref="PerspectiveCursorCompletion"/> per target stream so the rebuilder
  /// records a cursor for each row that was materialised. Rebuild-only: the live drain paths
  /// (<see cref="RunAsync"/>, <see cref="RunWithEventsAsync"/>) are unaffected.</para>
  ///
  /// <para>The default implementation delegates to <see cref="RunAsync"/> for a full replay and
  /// wraps the single completion in a list — so legacy/hand-written runners keep today's behaviour
  /// (no re-key). Generated runners override this with the partitioning implementation.</para>
  /// </summary>
  /// <param name="physicalStreamId">The stored stream whose events are replayed</param>
  /// <param name="perspectiveName">Name of the perspective to run</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>One cursor completion per materialised target stream</returns>
  async Task<IReadOnlyList<PerspectiveCursorCompletion>> RunRebuildAsync(
    Guid physicalStreamId,
    string perspectiveName,
    CancellationToken cancellationToken = default
  ) => [await RunAsync(physicalStreamId, perspectiveName, null, cancellationToken)];

  /// <summary>
  /// Rewinds a perspective by restoring from the nearest snapshot before the triggering event,
  /// then replays all events from that point in order. If no qualifying snapshot exists,
  /// performs a full replay from event zero.
  /// </summary>
  /// <param name="streamId">Stream to rewind</param>
  /// <param name="perspectiveName">Perspective to rewind</param>
  /// <param name="triggeringEventId">The late event that triggered the rewind (for snapshot selection)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Perspective cursor completion with processing status and last event ID</returns>
  Task<PerspectiveCursorCompletion> RewindAndRunAsync(
    Guid streamId,
    string perspectiveName,
    Guid triggeringEventId,
    CancellationToken cancellationToken = default
  );

  /// <summary>
  /// Slice 26.11 — commit-sequence-anchored rewind. When an inversion violator has a known
  /// <c>commit_sequence</c>, replay uses the commit-sequence snapshot anchor for full
  /// deterministic replay regardless of UUIDv7 generation timing.
  /// Default implementation falls through to <see cref="RewindAndRunAsync(Guid, string, Guid, CancellationToken)"/>
  /// for legacy runners.
  /// </summary>
  Task<PerspectiveCursorCompletion> RewindAndRunAsync(
    Guid streamId,
    string perspectiveName,
    Guid triggeringEventId,
    long? triggeringCommitSequence,
    CancellationToken cancellationToken = default
  ) => RewindAndRunAsync(streamId, perspectiveName, triggeringEventId, cancellationToken);

  /// <summary>
  /// Creates a bootstrap snapshot from the current model state.
  /// Called when a stream has processed events but has no snapshots yet.
  /// Idempotent — if snapshots already exist, this is never called.
  /// </summary>
  /// <param name="streamId">Stream to bootstrap</param>
  /// <param name="perspectiveName">Perspective to bootstrap</param>
  /// <param name="lastProcessedEventId">The cursor's last processed event ID to snapshot at</param>
  /// <param name="cancellationToken">Cancellation token</param>
  Task BootstrapSnapshotAsync(
    Guid streamId,
    string perspectiveName,
    Guid lastProcessedEventId,
    CancellationToken cancellationToken = default
  );
}
