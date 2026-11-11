// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  /// Reads the events <see cref="RunAsync"/> would apply: the stream's events after
  /// <paramref name="lastProcessedEventId"/>, of the types this perspective folds. <see cref="RunAsync"/> is exactly this
  /// read followed by <see cref="RunWithEventsAsync"/>, so a set-based batch reads a claimed pair's events here and
  /// prepares them (#2246).
  /// </summary>
  /// <remarks>The default returns null: a hand-written runner is run with <see cref="RunAsync"/> on the per-pair path.</remarks>
  /// <param name="streamId">Stream ID to read</param>
  /// <param name="lastProcessedEventId">Last event ID processed (null = from the beginning)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The events, in the order <see cref="RunAsync"/> reads them, or null when the runner does not expose its read.</returns>
  /// <docs>fundamentals/perspectives/write-modes#the-batch</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/IPerspectiveRunnerDefaultsTests.cs:ReadEventsAsync_Default_ReturnsNullAsync</tests>
  Task<IReadOnlyList<MessageEnvelope<IEvent>>?> ReadEventsAsync(
    Guid streamId,
    Guid? lastProcessedEventId,
    CancellationToken cancellationToken = default
  ) => Task.FromResult<IReadOnlyList<MessageEnvelope<IEvent>>?>(null);

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
    Status = PerspectiveProcessingStatus.None,
    RowChanged = false,  // the default implementation runs nothing, so it writes nothing
  });

  /// <summary>
  /// The first half of a set-based write: reads the row, filters and folds the batch exactly as
  /// <see cref="RunWithEventsAsync"/> does, and returns the write for the batch's write sink instead of writing it.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The caller holds the pair's apply lock from before this call until <see cref="CompleteAsync"/> returns
  /// (<see cref="PerspectiveApplyLockSet"/>); this method takes none, so a batch's locks are held across its write.
  /// Every prepared pair is completed with <see cref="CompleteAsync"/>, whether or not it carries a write.
  /// </para>
  /// <para>
  /// A pair the set-based write does not take is returned with a <see cref="PreparedPerspectiveApply.PerPairReason"/>,
  /// and <see cref="CompleteAsync"/> runs it through the per-pair path. The default implementation returns every pair
  /// that way (<see cref="PerspectivePerPairReason.RunnerDoesNotPrepare"/>), so a hand-written runner keeps working.
  /// </para>
  /// </remarks>
  /// <param name="streamId">Stream ID to process</param>
  /// <param name="perspectiveName">Name of the perspective to run</param>
  /// <param name="lastProcessedEventId">Last event ID processed (null = start from beginning)</param>
  /// <param name="events">The batch's events (already deserialized)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The prepared pair.</returns>
  /// <docs>fundamentals/perspectives/write-modes#prepare-and-commit</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/IPerspectiveRunnerDefaultsTests.cs:PrepareAsync_Default_HandsThePairToThePerPairPathAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectivePrepareCompleteIntegrationTests.cs</tests>
  Task<PreparedPerspectiveApply> PrepareAsync(
    Guid streamId,
    string perspectiveName,
    Guid? lastProcessedEventId,
    IReadOnlyList<MessageEnvelope<IEvent>> events,
    CancellationToken cancellationToken = default
  ) => PrepareAsync(streamId, perspectiveName, lastProcessedEventId, events, batchState: null, cancellationToken);

  /// <summary>
  /// <see cref="PrepareAsync(Guid, string, Guid?, IReadOnlyList{MessageEnvelope{IEvent}}, CancellationToken)"/>, reading
  /// the row from the batch's state source (<see cref="IPerspectiveStateSource"/>) where it can answer: no read at all
  /// for a stream the batch knows is new, and the batch's one read per perspective table for the rest.
  /// </summary>
  /// <param name="streamId">Stream ID to process</param>
  /// <param name="perspectiveName">Name of the perspective to run</param>
  /// <param name="lastProcessedEventId">Last event ID processed (null = start from beginning)</param>
  /// <param name="events">The batch's events (already deserialized)</param>
  /// <param name="batchState">The batch's reads, or null to read the row per stream.</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The prepared pair.</returns>
  /// <docs>fundamentals/perspectives/write-modes#state-source</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/IPerspectiveRunnerDefaultsTests.cs:PrepareAsync_Default_HandsThePairToThePerPairPathAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectivePrepareCompleteIntegrationTests.StateSource.cs</tests>
  Task<PreparedPerspectiveApply> PrepareAsync(
    Guid streamId,
    string perspectiveName,
    Guid? lastProcessedEventId,
    IReadOnlyList<MessageEnvelope<IEvent>> events,
    IPerspectiveBatchState? batchState,
    CancellationToken cancellationToken = default
  ) => Task.FromResult(PreparedPerspectiveApply.PerPair(
    streamId, perspectiveName, lastProcessedEventId, events, PerspectivePerPairReason.RunnerDoesNotPrepare));

  /// <summary>
  /// The second half of a set-based write: given the outcome of the prepared write, does what
  /// <see cref="RunWithEventsAsync"/> does after its write, and returns the same completion.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <see cref="PerspectiveWriteStatus.Written"/> takes the snapshot, fires the detached post-perspective stage and
  /// completes the pair. <see cref="PerspectiveWriteStatus.Refused"/> rewinds the stream, as a write the ordering guard
  /// refused does on the per-pair path (#1608). <see cref="PerspectiveWriteStatus.Conflict"/> runs the pair again
  /// through the per-pair path, with its retries on a moved row. <see cref="PerspectiveWriteStatus.Failed"/> is handled
  /// as a write that threw on the per-pair path, and the error is thrown to the caller.
  /// </para>
  /// <para>
  /// <paramref name="outcome"/> is required for a pair that carries a <see cref="PreparedPerspectiveApply.Write"/>
  /// and ignored otherwise. The caller still holds the pair's apply lock. The default implementation runs a per-pair
  /// pair through <see cref="RunWithEventsAsync"/>.
  /// </para>
  /// </remarks>
  /// <param name="prepared">The pair <see cref="PrepareAsync"/> returned, from this runner.</param>
  /// <param name="outcome">The outcome of its write, or null when it carries none.</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The pair's cursor completion.</returns>
  /// <docs>fundamentals/perspectives/write-modes#prepare-and-commit</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/IPerspectiveRunnerDefaultsTests.cs:CompleteAsync_Default_RunsAPerPairPairThroughRunWithEventsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectivePrepareCompleteIntegrationTests.cs</tests>
  Task<PerspectiveCursorCompletion> CompleteAsync(
    PreparedPerspectiveApply prepared,
    PerspectiveWriteOutcome? outcome,
    CancellationToken cancellationToken = default
  ) {
    ArgumentNullException.ThrowIfNull(prepared);
    if (!prepared.RunsPerPair) {
      throw new ArgumentException(
        $"{GetType().Name} prepares no writes, so it completes only a pair it handed to the per-pair path.", nameof(prepared));
    }
    return RunWithEventsAsync(
      prepared.StreamId, prepared.PerspectiveName, prepared.LastProcessedEventId, prepared.Events, cancellationToken);
  }

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
  /// Rebuilds a stream archived through a prefix without reading the archive (#1931): restores the stream's newest live
  /// snapshot and replays the hot events after it. Returns <see langword="null"/> when the stream has no snapshot this
  /// runner can read, so the rebuild skips the stream. The newest snapshot is always live: an archive refuses a prefix that
  /// reaches the newest snapshot any perspective holds for the stream.
  /// </summary>
  /// <remarks>The default implementation has no snapshot store, so it returns <see langword="null"/>.</remarks>
  /// <param name="streamId">The stream to rebuild.</param>
  /// <param name="perspectiveName">Name of the perspective to run.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The stream's cursor completion, or <see langword="null"/> when there is no live snapshot to start from.</returns>
  /// <docs>fundamentals/perspectives/rebuild#archived-data</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/ArchiveReadThroughIntegrationTests.cs:Rebuild_ByDefault_StartsAStreamArchivedThroughAPrefix_FromItsNewestLiveSnapshot_AndReadsNoArchivedEventAsync</tests>
  Task<IReadOnlyList<PerspectiveCursorCompletion>?> RunRebuildFromLatestSnapshotAsync(
    Guid streamId,
    string perspectiveName,
    CancellationToken cancellationToken = default
  ) => Task.FromResult<IReadOnlyList<PerspectiveCursorCompletion>?>(null);

  /// <summary>
  /// Folds a row that receives events an upcaster re-keyed onto it during a REBUILD: the row's own stream's events
  /// (less any re-keyed away) together with every re-keyed source, merged in commit order
  /// (<see cref="RebuildRowRouting.Merge"/>) and folded from an empty model (#1820).
  /// </summary>
  /// <remarks>
  /// The default implementation has no event store to read the row's own stream from, so it folds the sources alone.
  /// Generated runners read the row's own stream too.
  /// </remarks>
  /// <param name="rowStreamId">The row's stream.</param>
  /// <param name="perspectiveName">Name of the perspective to run.</param>
  /// <param name="rekeyedSources">The re-keyed events, one list per stream they were read from.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The row's cursor completion.</returns>
  /// <docs>fundamentals/perspectives/perspectives#rebuild</docs>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/RebuildRowRoutingTests.cs:RunRekeyedRowAsync_Default_FoldsTheMergedSourcesAsync</tests>
  async Task<PerspectiveCursorCompletion> RunRekeyedRowAsync(
    Guid rowStreamId,
    string perspectiveName,
    IReadOnlyList<IReadOnlyList<MessageEnvelope<IEvent>>> rekeyedSources,
    CancellationToken cancellationToken = default
  ) => await RunWithEventsAsync(rowStreamId, perspectiveName, null, RebuildRowRouting.Merge(rekeyedSources), cancellationToken);

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
