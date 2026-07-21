using Microsoft.Extensions.Logging;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Perspectives;

/// <summary>The outcome of an E3 Tier-2 compaction.</summary>
/// <param name="Status">
/// <c>compacted</c> (folded + truncated) | <c>no_snapshot</c> (no authoritative model to fold) |
/// <c>no_anchor</c> (the snapshot's anchor event has no resolvable version) | or the underlying
/// <see cref="StreamCloseResult.Status"/> if the truncate did not proceed (<c>blocked</c> / <c>full_history_blocked</c> / …).
/// </param>
/// <param name="ThroughVersion">The per-stream version folded through (0 unless a fold was attempted).</param>
/// <param name="EventsFolded">How many detail events were truncated.</param>
/// <docs>fundamentals/events/ephemeral-events</docs>
public sealed record CompactionResult(string Status, long ThroughVersion, long EventsFolded);

/// <summary>
/// E3 (Carry-forward / Tier-2) — folds an ephemeral stream's detail into an authoritative
/// <see cref="Compacted"/> carry-forward. Composes the pieces that already exist: read the authoritative
/// perspective snapshot, write it as a <see cref="Compacted"/> origin at the stream head (summary durable
/// BEFORE the truncate), then gated-truncate the folded detail via the A1 <see cref="IStreamCloser"/>. The
/// compacted stream then replays only back to the <see cref="Compacted"/> event.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public interface IStreamCompactor {
  /// <summary>Compact <paramref name="streamId"/> to the authoritative model of <paramref name="perspectiveName"/>.</summary>
  Task<CompactionResult> CompactAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IStreamCompactor" />
/// <docs>fundamentals/events/ephemeral-events</docs>
public sealed partial class StreamCompactor : IStreamCompactor {
  private readonly IPerspectiveSnapshotStore _snapshots;
  private readonly IWorkCoordinator _coordinator;
  private readonly IEventStore _eventStore;
  private readonly IStreamCloser _closer;
  private readonly ILogger<StreamCompactor> _logger;

  /// <summary>Creates a compactor over the snapshot store, coordinator, event store, and A1 closer.</summary>
  public StreamCompactor(
      IPerspectiveSnapshotStore snapshots, IWorkCoordinator coordinator, IEventStore eventStore,
      IStreamCloser closer, ILogger<StreamCompactor> logger) {
    _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
    _closer = closer ?? throw new ArgumentNullException(nameof(closer));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <inheritdoc />
  public async Task<CompactionResult> CompactAsync(
      Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrEmpty(perspectiveName);

    // 1. The authoritative model — there is nothing to fold without a snapshot (E1 drives one before a reap).
    var snapshot = await _snapshots
      .GetLatestSnapshotWithCommitSequenceAsync(streamId, perspectiveName, cancellationToken).ConfigureAwait(false);
    if (snapshot is null) {
      LogNoSnapshot(_logger, streamId, perspectiveName);
      return new CompactionResult("no_snapshot", 0, 0);
    }

    // 2. Fold through the snapshot's anchor event version — everything at/below it is the folded detail.
    var throughVersion = await _coordinator
      .GetEventVersionAsync(snapshot.Value.SnapshotEventId, cancellationToken).ConfigureAwait(false);
    if (throughVersion is null) {
      LogNoAnchor(_logger, streamId, snapshot.Value.SnapshotEventId);
      return new CompactionResult("no_anchor", 0, 0);
    }

    // 3. Extract the model + its schema version from the versioned snapshot blob.
    _ = VersionedJsonEnvelope.TryRead(snapshot.Value.SnapshotData, out var schemaVersion, out var model);

    // 4. Write the Compacted origin FIRST (summary durable before the truncate — no state loss on failure).
    var compacted = new Compacted {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      Model = model,
      SchemaVersion = schemaVersion,
      ThroughVersion = throughVersion.Value,
    };
    // Compacted is ICompactedEvent, so the flag deriver stamps EventFlags.Compacted (permanent StateBased) —
    // the reaper (self-destruct = flags&8) never targets it. The authoritative origin is protected BY MODE;
    // no hold-at-infinity is needed (the design-review payoff of the StateBased factoring).
    await _eventStore.AppendAsync(streamId, compacted, cancellationToken).ConfigureAwait(false);

    // 5. Truncate the folded detail (discard — the data was ephemeral, so there is no archive). The Compacted
    //    event sits above throughVersion, so it survives as the head origin; the A1 closer's consumption gate
    //    ensures the folded detail was actually consumed before it goes.
    var close = await _closer.CloseAsync(streamId, throughVersion.Value, archive: false, cancellationToken)
      .ConfigureAwait(false);

    var status = string.Equals(close.Status, "closed", StringComparison.Ordinal) ? "compacted" : close.Status;
    LogCompacted(_logger, streamId, perspectiveName, throughVersion.Value, status, close.EventsTruncated);
    return new CompactionResult(status, throughVersion.Value, close.EventsTruncated);
  }

  [LoggerMessage(EventId = 50, Level = LogLevel.Information,
    Message = "Compaction of stream {StreamId}/{PerspectiveName} skipped: no authoritative snapshot to fold")]
  static partial void LogNoSnapshot(ILogger logger, Guid streamId, string perspectiveName);

  [LoggerMessage(EventId = 51, Level = LogLevel.Warning,
    Message = "Compaction of stream {StreamId} skipped: snapshot anchor event {AnchorEventId} has no resolvable version")]
  static partial void LogNoAnchor(ILogger logger, Guid streamId, Guid anchorEventId);

  [LoggerMessage(EventId = 52, Level = LogLevel.Information,
    Message = "Compacted stream {StreamId}/{PerspectiveName} through version {ThroughVersion}: {Status}, {EventsFolded} folded")]
  static partial void LogCompacted(ILogger logger, Guid streamId, string perspectiveName, long throughVersion, string status, long eventsFolded);
}
