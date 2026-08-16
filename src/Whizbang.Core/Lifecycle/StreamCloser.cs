using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// A1 (Archival &amp; Compaction) — "close the books" on a durable Sourced stream, firing the E2 destruction
/// hook around the truncate so a developer can preserve the carry-forward / archive before the detail goes.
/// A close is a stream-granularity destruction (<see cref="DestructionReason.PeriodClose"/>), so it reuses
/// the same <see cref="IDestructionHook"/> the ephemeral reaper uses.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public interface IStreamCloser {
  /// <summary>
  /// Close <paramref name="streamId"/> through <paramref name="throughVersion"/>: fire the awaited
  /// <c>PreDestruction</c> hook (where a receptor commits the carry-forward / runs archive logic on the
  /// critical path), then gated-truncate the detail, then fire the detached <c>PostDestruction</c> hook. A
  /// hook that cancels or defers vetoes the close; a throwing pre-hook aborts it (durable detail is never
  /// truncated when the preserve-work failed).
  /// </summary>
  Task<StreamCloseResult> CloseAsync(
    Guid streamId, long throughVersion, bool archive = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IStreamCloser"/>: orchestrates the E2 destruction hook around
/// <see cref="IWorkCoordinator.CloseStreamAsync"/>. Inert-friendly — with no <see cref="IDestructionHook"/>
/// registered it is a thin pass-through to the gated truncate.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public sealed partial class StreamCloser : IStreamCloser {
  private readonly IWorkCoordinator _coordinator;
  private readonly ILogger<StreamCloser> _logger;
  private readonly IDestructionHook? _hook;

  /// <summary>Creates a closer over the coordinator, with an optional destruction hook.</summary>
  public StreamCloser(IWorkCoordinator coordinator, ILogger<StreamCloser> logger, IDestructionHook? hook = null) {
    _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _hook = hook;
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/StreamCloserFoldOrderTests.cs:Close_FoldsTheApplyPath_BeforeTheTruncateAsync</tests>
  public async Task<StreamCloseResult> CloseAsync(
      Guid streamId, long throughVersion, bool archive = false, CancellationToken cancellationToken = default) {
    // A1-6b full-history guard: a DISCARD close (archive:false) that would strand a [FullHistory] projection —
    // one that consumes an event in the truncated range and cannot resume from the closing event — is refused
    // (it could never rebuild). An archiving close is always safe (detail retrievable), so it skips the check.
    if (!archive) {
      var consumers = await _coordinator.GetConsumingPerspectiveNamesAsync(streamId, throughVersion, cancellationToken)
        .ConfigureAwait(false);
      if (Whizbang.Core.Perspectives.FullHistoryPerspectiveRegistry.AnyFullHistory(consumers)) {
        LogFullHistoryBlocked(_logger, streamId);
        return new StreamCloseResult("full_history_blocked", 0);
      }
    }

    var context = new DestructionContext {
      Reason = DestructionReason.PeriodClose,
      Granularity = DestructionGranularity.Stream,
      StreamId = streamId,
    };

    // PreDestruction (awaited, critical path): the hook commits the carry-forward / archive BEFORE the
    // truncate. Cancel or Defer vetoes the close (nothing is truncated). A THROWING pre-hook aborts the
    // close — durable Sourced detail must never be truncated when the preserve-work failed (no fail-open,
    // unlike the ephemeral reaper whose retry-then-forced-delete can't leak durable data).
    if (_hook is not null) {
      DestructionResult decision;
      try {
        decision = await _hook.OnBeforeDestructionAsync(context, cancellationToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        LogPreCloseFailed(_logger, ex, streamId);
        throw;
      }
      if (decision.Cancel) {
        LogCloseVetoed(_logger, streamId, "cancelled");
        return new StreamCloseResult("cancelled", 0);
      }
      if (decision.DeferUntil.HasValue) {
        LogCloseVetoed(_logger, streamId, "deferred");
        return new StreamCloseResult("deferred", 0);
      }
    }

    // Fold-before-discard (apply-stack lineage): the close is about to truncate this stream's
    // pointers, so its collapsed path folds into the persisted signature counts first. The stream
    // dies; its shape survives. Known v1 caveat: re-closing an already-closed stream re-folds the
    // surviving carry-forward — a small, bounded distortion accepted until per-stream fold
    // watermarks exist.
    _ = await _coordinator.FoldStreamApplyPathsAsync([streamId], cancellationToken).ConfigureAwait(false);

    var result = await _coordinator.CloseStreamAsync(streamId, throughVersion, archive, cancellationToken)
      .ConfigureAwait(false);

    // PostDestruction (after the truncate committed): notify / metrics / cascade. Non-fatal, and only on an
    // actual close (not a gate-blocked / no-carry-forward / debug-skipped outcome).
    if (_hook is not null && string.Equals(result.Status, "closed", StringComparison.Ordinal)) {
      try {
        await _hook.OnAfterDestructionAsync(context, cancellationToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        LogPostCloseFailed(_logger, ex, streamId);
      }
    }
    return result;
  }

  [LoggerMessage(EventId = 40, Level = LogLevel.Error,
    Message = "PreDestruction close hook threw for stream {StreamId}; close aborted (detail not truncated)")]
  static partial void LogPreCloseFailed(ILogger logger, Exception ex, Guid streamId);

  [LoggerMessage(EventId = 43, Level = LogLevel.Warning,
    Message = "Discard-close of stream {StreamId} refused: a [FullHistory] projection consumes it — archive instead (archive: true)")]
  static partial void LogFullHistoryBlocked(ILogger logger, Guid streamId);

  [LoggerMessage(EventId = 41, Level = LogLevel.Information,
    Message = "Close of stream {StreamId} was {Outcome} by the PreDestruction hook")]
  static partial void LogCloseVetoed(ILogger logger, Guid streamId, string outcome);

  [LoggerMessage(EventId = 42, Level = LogLevel.Warning,
    Message = "PostDestruction close hook threw for stream {StreamId}; close already committed")]
  static partial void LogPostCloseFailed(ILogger logger, Exception ex, Guid streamId);
}
