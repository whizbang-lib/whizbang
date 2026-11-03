// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// A1 (Archival &amp; Compaction): the gated prefix truncate of a durable Sourced stream, firing the E2
/// destruction hook around it so a developer can preserve the carry-forward / archive before the detail goes.
/// A prefix truncate is a stream-granularity destruction (<see cref="DestructionReason.PeriodClose"/>), so it
/// reuses the same <see cref="IDestructionHook"/> the ephemeral reaper uses. Named for what it does since
/// #1731 (it was <c>IStreamCloser</c>), which leaves "close" to closing the books.
/// </summary>
/// <docs>fundamentals/events/event-streams</docs>
public interface IStreamPrefixTruncator {
  /// <summary>
  /// Truncate <paramref name="streamId"/> through <paramref name="throughVersion"/>: fire the awaited
  /// <c>PreDestruction</c> hook (where a receptor commits the carry-forward / runs archive logic on the
  /// critical path), then gated-truncate the detail, then fire the detached <c>PostDestruction</c> hook. A
  /// hook that cancels or defers vetoes the truncate; a throwing pre-hook aborts it (durable detail is never
  /// truncated when the preserve-work failed).
  /// </summary>
  /// <remarks>
  /// The destruction lifecycle stages run around the truncate as they do around the ephemeral reap: the events at
  /// or below <paramref name="throughVersion"/> whose type has a destruction-stage receptor are loaded first, the
  /// <c>PreDestructionDetached</c> and <c>PreDestructionInline</c> receptors run before the truncate (a throwing Pre
  /// Inline receptor aborts the truncate, as a throwing pre-hook does), and the <c>PostDestructionDetached</c> and
  /// <c>PostDestructionInline</c> receptors run with the same envelopes after a truncate that happened.
  /// </remarks>
  /// <param name="streamId">The stream.</param>
  /// <param name="throughVersion">The last version removed.</param>
  /// <param name="disposition">What happens to the detail (#1735), unless the PreDestruction hook returns another:
  /// <see cref="Disposition.Delete"/> (the default) discards it; <see cref="Disposition.Archive"/> writes it through the
  /// registered <see cref="IArchiveStore"/>; <see cref="Disposition.Compact"/> runs the registered
  /// <see cref="ICompactionHandler"/> and then discards it, and with none registered keeps it
  /// (<see cref="StreamTruncateStatus.COMPACTION_UNAVAILABLE"/>); <see cref="Disposition.CryptoShred"/> keeps it
  /// (<see cref="StreamTruncateStatus.CRYPTO_SHRED_UNAVAILABLE"/>).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The truncate's outcome.</returns>
  /// <docs>fundamentals/events/stream-end-of-life#dispositions</docs>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/StreamPrefixTruncatorDispositionTests.cs</tests>
  Task<StreamTruncateResult> TruncateAsync(
    Guid streamId, long throughVersion, Disposition disposition = Disposition.Delete, CancellationToken cancellationToken = default);

  /// <summary>
  /// <see cref="TruncateAsync(Guid, long, Disposition, CancellationToken)"/> for a reason other than a period close (#1740):
  /// the hook and the destruction stages see <paramref name="reason"/>, for example
  /// <see cref="DestructionReason.VersionCapExceeded"/> when an end-of-life policy's version cap cuts the prefix.
  /// </summary>
  /// <param name="streamId">The stream.</param>
  /// <param name="throughVersion">The last version removed.</param>
  /// <param name="disposition">What happens to the detail, unless the PreDestruction hook returns another.</param>
  /// <param name="reason">Why the prefix goes, as the hook sees it.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The truncate's outcome.</returns>
  /// <docs>fundamentals/events/stream-end-of-life#policies</docs>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/StreamPrefixTruncatorDispositionTests.cs</tests>
  Task<StreamTruncateResult> TruncateAsync(
    Guid streamId, long throughVersion, Disposition disposition, DestructionReason reason, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IStreamPrefixTruncator"/>: orchestrates the E2 destruction hook and the destruction lifecycle
/// stages (#1504) around <see cref="IWorkCoordinator.TruncateStreamPrefixAsync"/>. Inert-friendly: with no
/// <see cref="IDestructionHook"/> and no destruction-stage receptor registered it is a thin pass-through to the
/// gated truncate.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
/// <remarks>Creates a truncator over the coordinator, with its destruction hook (the default proceeds and observes nothing),
/// the scope factory each truncate resolves its destruction-stage receptors, event store and invoker from, and the archive
/// store an archiving truncate writes through (WS-3 S7). With no archive store, an archiving truncate copies into
/// <c>wh_event_archive</c> through the coordinator, as it did before the store existed.</remarks>
public sealed partial class StreamPrefixTruncator(IWorkCoordinator coordinator, ILogger<StreamPrefixTruncator> logger, IDestructionHook hook,
    IServiceScopeFactory scopeFactory, IArchiveStore? archiveStore) : IStreamPrefixTruncator {
  private readonly IWorkCoordinator _coordinator = ArgumentGuard.NotNull(coordinator);
  private readonly ILogger<StreamPrefixTruncator> _logger = ArgumentGuard.NotNull(logger);
  private readonly IDestructionHook _hook = hook;
  private readonly IServiceScopeFactory _scopeFactory = ArgumentGuard.NotNull(scopeFactory);
  private readonly IArchiveStore? _archiveStore = archiveStore;

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/StreamPrefixTruncatorFoldOrderTests.cs:Truncate_FoldsTheApplyPath_BeforeTheTruncateAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Lifecycle/StreamPrefixTruncatorDestructionStageComponentTests.cs</tests>
  public Task<StreamTruncateResult> TruncateAsync(
      Guid streamId, long throughVersion, Disposition disposition = Disposition.Delete, CancellationToken cancellationToken = default) =>
    TruncateAsync(streamId, throughVersion, disposition, DestructionReason.PeriodClose, cancellationToken);

  /// <inheritdoc />
  public async Task<StreamTruncateResult> TruncateAsync(
      Guid streamId, long throughVersion, Disposition disposition, DestructionReason reason, CancellationToken cancellationToken = default) {
    var context = new DestructionContext {
      Reason = reason,
      Granularity = DestructionGranularity.Stream,
      StreamId = streamId,
      ThroughVersion = throughVersion,
      DeclaredDefault = disposition,
    };

    // The destruction-stage receptors (#1504) run around the truncate the way they run around the ephemeral
    // reap: the events through the truncate point whose type has a receptor are loaded before anything is
    // destroyed, and the Post stages get the same envelopes once the stored data is gone.
    await using var scope = _scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    var envelopes = await _loadDestructionStageEnvelopesAsync(services, streamId, throughVersion, cancellationToken)
      .ConfigureAwait(false);

    // PreDestruction (awaited, critical path): the hook commits the carry-forward / archive BEFORE the
    // truncate. Cancel or Defer vetoes the truncate (nothing is truncated). A THROWING pre-hook aborts the
    // truncate: durable Sourced detail must never be truncated when the preserve-work failed (no fail-open,
    // unlike the ephemeral reaper whose retry-then-forced-delete can't leak durable data). Otherwise the hook's
    // disposition runs, or the declared one when it names none (#1735).
    var decision = await _decideAsync(context, streamId, cancellationToken).ConfigureAwait(false);
    if (decision.Veto is { } veto) {
      return veto;
    }
    var effective = decision.Disposition;
    var compactor = effective == Disposition.Compact ? services.GetService<ICompactionHandler>() : null;
    if (_unavailable(effective, compactor, streamId, throughVersion) is { } unavailable) {
      return unavailable;
    }

    // A1-6b full-history guard: a truncate that discards the detail and would strand a [FullHistory] projection,
    // one that consumes an event in the truncated range and cannot resume from the closing event, is refused
    // (it could never rebuild). An archiving truncate is always safe (detail retrievable), so it skips the check.
    // Checked on the disposition that runs, so a hook cannot get around it by switching an archive to a discard.
    var archive = effective == Disposition.Archive;
    if (!archive && await _strandsAFullHistoryProjectionAsync(streamId, throughVersion, cancellationToken).ConfigureAwait(false)) {
      return new StreamTruncateResult("full_history_blocked", 0);
    }

    // The Pre stages, after the hook proceeded: a throwing Pre Inline receptor aborts the truncate exactly as a
    // throwing pre-hook does
    try {
      await DestructionStageReceptors.FirePreAsync(services, envelopes, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogPreStageFailed(_logger, ex, streamId);
      throw;
    }

    // Compact: the developer's handler folds the detail into its summary, durably, before the detail is discarded.
    // A throwing handler aborts the truncate, as a throwing pre-hook does.
    if (compactor is not null) {
      await compactor.CompactAsync(context, cancellationToken).ConfigureAwait(false);
    }

    // Fold-before-discard (apply-stack lineage): the truncate is about to remove this stream's
    // pointers, so its collapsed path folds into the persisted signature counts first. The stream
    // dies; its shape survives. Known v1 caveat: re-truncating an already-truncated stream re-folds the
    // surviving carry-forward: a small, bounded distortion accepted until per-stream fold
    // watermarks exist.
    _ = await _coordinator.FoldStreamApplyPathsAsync([streamId], cancellationToken).ConfigureAwait(false);

    var result = archive && _archiveStore is not null
      ? await _archiveThroughStoreAsync(_archiveStore, streamId, throughVersion, cancellationToken).ConfigureAwait(false)
      : await _coordinator.TruncateStreamPrefixAsync(streamId, throughVersion, archive, cancellationToken).ConfigureAwait(false);

    // PostDestruction (after the truncate committed): notify / metrics / cascade. Non-fatal, and only on an
    // actual truncate (not a gate-blocked / no-carry-forward / debug-skipped outcome).
    if (string.Equals(result.Status, "truncated", StringComparison.Ordinal)) {
      await _announceTruncatedAsync(context, services, envelopes, streamId, cancellationToken).ConfigureAwait(false);
    }
    return result;
  }

  /// <summary>
  /// An archiving truncate through the registered <see cref="IArchiveStore"/> (WS-3 S7): a prefix batch, so the
  /// truncate's guards apply unchanged, reported in the truncate's own vocabulary (<c>archived</c> reads as
  /// <c>truncated</c>; a refusal keeps its name).
  /// </summary>
  /// <docs>fundamentals/events/archive-store#the-prefix-truncate-archives-through-the-store</docs>
  private static async Task<StreamTruncateResult> _archiveThroughStoreAsync(
      IArchiveStore store, Guid streamId, long throughVersion, CancellationToken cancellationToken) {
    var written = await store.WriteAsync(ArchiveBatch.Prefix(streamId, throughVersion), cancellationToken).ConfigureAwait(false);
    return written.Archived
      ? new StreamTruncateResult("truncated", written.EventsArchived)
      : new StreamTruncateResult(written.Status, 0);
  }

  /// <summary>
  /// The events the truncate is about to remove whose type has a destruction-stage receptor, loaded before the
  /// truncate. With no destruction-stage receptor registered, the truncated range is not even listed.
  /// </summary>
  private async Task<IReadOnlyList<Whizbang.Core.Observability.MessageEnvelope<IEvent>>> _loadDestructionStageEnvelopesAsync(
      IServiceProvider services, Guid streamId, long throughVersion, CancellationToken cancellationToken) {
    var stageTypes = DestructionStageReceptors.TypesWithReceptors(services);
    if (stageTypes.Count == 0) {
      return [];
    }
    var targets = await _coordinator.GetStreamTruncateTargetsAsync(streamId, throughVersion, cancellationToken).ConfigureAwait(false);
    return await DestructionStageReceptors.LoadAsync(services, targets, stageTypes, cancellationToken).ConfigureAwait(false);
  }

  [LoggerMessage(EventId = 44, Level = LogLevel.Error,
    Message = "A PreDestructionInline receptor threw for stream {StreamId}; truncate aborted (detail kept)")]
  static partial void LogPreStageFailed(ILogger logger, Exception ex, Guid streamId);

  [LoggerMessage(EventId = 45, Level = LogLevel.Warning,
    Message = "A PostDestructionInline receptor threw for stream {StreamId}; truncate already committed")]
  static partial void LogPostStageFailed(ILogger logger, Exception ex, Guid streamId);

  [LoggerMessage(EventId = 40, Level = LogLevel.Error,
    Message = "PreDestruction hook threw for stream {StreamId}; truncate aborted (detail kept)")]
  static partial void LogPreHookFailed(ILogger logger, Exception ex, Guid streamId);

  [LoggerMessage(EventId = 43, Level = LogLevel.Warning,
    Message = "Discarding truncate of stream {StreamId} refused: a [FullHistory] projection consumes it; archive instead (Disposition.Archive)")]
  static partial void LogFullHistoryBlocked(ILogger logger, Guid streamId);

  [LoggerMessage(EventId = 41, Level = LogLevel.Information,
    Message = "Truncate of stream {StreamId} was {Outcome} by the PreDestruction hook")]
  static partial void LogTruncateVetoed(ILogger logger, Guid streamId, string outcome);

  [LoggerMessage(EventId = 42, Level = LogLevel.Warning,
    Message = "PostDestruction hook threw for stream {StreamId}; truncate already committed")]
  static partial void LogPostHookFailed(ILogger logger, Exception ex, Guid streamId);

  // The PostDestruction hook and the Post stages, after a truncate that committed: a failure is reported, never thrown.
  private async Task _announceTruncatedAsync(DestructionContext context, IServiceProvider services,
      IReadOnlyList<Whizbang.Core.Observability.MessageEnvelope<IEvent>> envelopes, Guid streamId, CancellationToken cancellationToken) {
    try {
      await _hook.OnAfterDestructionAsync(context, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      LogPostHookFailed(_logger, ex, streamId);
    }
    // The Post stages, with the envelopes loaded before the truncate. The truncate committed, so a failing
    // receptor is reported, never thrown
    try {
      await DestructionStageReceptors.FirePostAsync(services, envelopes, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogPostStageFailed(_logger, ex, streamId);
    }
  }

  // A [FullHistory] projection consumes an event in the range and cannot resume from the closing event, so discarding
  // the range would strand it (it could never rebuild); logged when it does.
  private async Task<bool> _strandsAFullHistoryProjectionAsync(Guid streamId, long throughVersion, CancellationToken cancellationToken) {
    var consumers = await _coordinator.GetConsumingPerspectiveNamesAsync(streamId, throughVersion, cancellationToken)
      .ConfigureAwait(false);
    if (!Whizbang.Core.Perspectives.FullHistoryPerspectiveRegistry.AnyFullHistory(consumers)) {
      return false;
    }
    LogFullHistoryBlocked(_logger, streamId);
    return true;
  }

  /// <summary>
  /// Runs the PreDestruction hook: a veto when it cancels or defers the truncate, otherwise the disposition to run (the
  /// hook's, or the declared one when it names none). A throwing hook aborts the truncate (rethrown after logging).
  /// </summary>
  private async Task<(StreamTruncateResult? Veto, Disposition Disposition)> _decideAsync(
      DestructionContext context, Guid streamId, CancellationToken cancellationToken) {
    DestructionResult decision;
    try {
      decision = await _hook.OnBeforeDestructionAsync(context, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      LogPreHookFailed(_logger, ex, streamId);
      throw;
    }
    if (decision.Cancel) {
      LogTruncateVetoed(_logger, streamId, "canceled");
      return (new StreamTruncateResult("canceled", 0), context.DeclaredDefault);
    }
    if (decision.DeferUntil.HasValue) {
      LogTruncateVetoed(_logger, streamId, "deferred");
      return (new StreamTruncateResult("deferred", 0), context.DeclaredDefault);
    }
    return (null, decision.Disposition ?? context.DeclaredDefault);
  }

  /// <summary>
  /// A disposition this truncate cannot carry out keeps the data and says why: <see cref="Disposition.CryptoShred"/>
  /// has not shipped, and <see cref="Disposition.Compact"/> needs a registered <see cref="ICompactionHandler"/>.
  /// </summary>
  private StreamTruncateResult? _unavailable(Disposition disposition, ICompactionHandler? compactor, Guid streamId, long throughVersion) {
    if (disposition == Disposition.CryptoShred) {
      LogCryptoShredUnavailable(_logger, streamId, throughVersion);
      return new StreamTruncateResult(StreamTruncateStatus.CRYPTO_SHRED_UNAVAILABLE, 0);
    }
    if (disposition == Disposition.Compact && compactor is null) {
      LogCompactionUnavailable(_logger, streamId, throughVersion);
      return new StreamTruncateResult(StreamTruncateStatus.COMPACTION_UNAVAILABLE, 0);
    }
    return null;
  }

  [LoggerMessage(EventId = 46, Level = LogLevel.Warning,
    Message = "Truncate of stream {StreamId} through version {ThroughVersion} kept the data: its disposition is Compact, which needs an ICompactionHandler registered, and none is")]
  static partial void LogCompactionUnavailable(ILogger logger, Guid streamId, long throughVersion);

  [LoggerMessage(EventId = 47, Level = LogLevel.Warning,
    Message = "Truncate of stream {StreamId} through version {ThroughVersion} kept the data: its disposition is CryptoShred, which has not shipped")]
  static partial void LogCryptoShredUnavailable(ILogger logger, Guid streamId, long throughVersion);
}
