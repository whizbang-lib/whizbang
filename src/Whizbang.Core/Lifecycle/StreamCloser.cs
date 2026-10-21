// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// A1 (Archival &amp; Compaction) — "close the books" on a durable Sourced stream, firing the E2 destruction
/// hook around the truncate so a developer can preserve the carry-forward / archive before the detail goes.
/// A close is a stream-granularity destruction (<see cref="DestructionReason.PeriodClose"/>), so it reuses
/// the same <see cref="IDestructionHook"/> the ephemeral reaper uses.
/// </summary>
/// <docs>fundamentals/events/event-streams</docs>
public interface IStreamCloser {
  /// <summary>
  /// Close <paramref name="streamId"/> through <paramref name="throughVersion"/>: fire the awaited
  /// <c>PreDestruction</c> hook (where a receptor commits the carry-forward / runs archive logic on the
  /// critical path), then gated-truncate the detail, then fire the detached <c>PostDestruction</c> hook. A
  /// hook that cancels or defers vetoes the close; a throwing pre-hook aborts it (durable detail is never
  /// truncated when the preserve-work failed).
  /// </summary>
  /// <remarks>
  /// The destruction lifecycle stages run around the truncate as they do around the ephemeral reap: the events at
  /// or below <paramref name="throughVersion"/> whose type has a destruction-stage receptor are loaded first, the
  /// <c>PreDestructionDetached</c> and <c>PreDestructionInline</c> receptors run before the truncate (a throwing Pre
  /// Inline receptor aborts the close, as a throwing pre-hook does), and the <c>PostDestructionDetached</c> and
  /// <c>PostDestructionInline</c> receptors run with the same envelopes after a close that truncated.
  /// </remarks>
  Task<StreamCloseResult> CloseAsync(
    Guid streamId, long throughVersion, bool archive = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IStreamCloser"/>: orchestrates the E2 destruction hook and the destruction lifecycle
/// stages (#1504) around <see cref="IWorkCoordinator.CloseStreamAsync"/>. Inert-friendly: with no
/// <see cref="IDestructionHook"/> and no destruction-stage receptor registered it is a thin pass-through to the
/// gated truncate.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
/// <remarks>Creates a closer over the coordinator, with its destruction hook (the default proceeds and observes nothing) and
/// the scope factory each close resolves its destruction-stage receptors, event store and invoker from.</remarks>
public sealed partial class StreamCloser(IWorkCoordinator coordinator, ILogger<StreamCloser> logger, IDestructionHook hook,
    IServiceScopeFactory scopeFactory) : IStreamCloser {
  private readonly IWorkCoordinator _coordinator = ArgumentGuard.NotNull(coordinator);
  private readonly ILogger<StreamCloser> _logger = ArgumentGuard.NotNull(logger);
  private readonly IDestructionHook _hook = hook;
  private readonly IServiceScopeFactory _scopeFactory = ArgumentGuard.NotNull(scopeFactory);

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/StreamCloserFoldOrderTests.cs:Close_FoldsTheApplyPath_BeforeTheTruncateAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Lifecycle/StreamCloserDestructionStageComponentTests.cs</tests>
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

    // The destruction-stage receptors (#1504) run around the truncate the way they run around the ephemeral
    // reap: the events through the close point whose type has a receptor are loaded before anything is
    // destroyed, and the Post stages get the same envelopes once the stored data is gone.
    await using var scope = _scopeFactory.CreateAsyncScope();
    var services = scope.ServiceProvider;
    var envelopes = await _loadDestructionStageEnvelopesAsync(services, streamId, throughVersion, cancellationToken)
      .ConfigureAwait(false);

    // PreDestruction (awaited, critical path): the hook commits the carry-forward / archive BEFORE the
    // truncate. Cancel or Defer vetoes the close (nothing is truncated). A THROWING pre-hook aborts the
    // close — durable Sourced detail must never be truncated when the preserve-work failed (no fail-open,
    // unlike the ephemeral reaper whose retry-then-forced-delete can't leak durable data).
    if (await _vetoedByPreDestructionAsync(context, streamId, cancellationToken).ConfigureAwait(false) is { } veto) {
      return veto;
    }

    // The Pre stages, after the hook proceeded: a throwing Pre Inline receptor aborts the close exactly as a
    // throwing pre-hook does
    try {
      await DestructionStageReceptors.FirePreAsync(services, envelopes, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogPreStageFailed(_logger, ex, streamId);
      throw;
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
    if (string.Equals(result.Status, "closed", StringComparison.Ordinal)) {
      try {
        await _hook.OnAfterDestructionAsync(context, cancellationToken).ConfigureAwait(false);
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        LogPostCloseFailed(_logger, ex, streamId);
      }
      // The Post stages, with the envelopes loaded before the truncate. The close committed, so a failing
      // receptor is reported, never thrown
      try {
        await DestructionStageReceptors.FirePostAsync(services, envelopes, cancellationToken).ConfigureAwait(false);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogPostStageFailed(_logger, ex, streamId);
      }
    }
    return result;
  }

  /// <summary>
  /// The events the close is about to truncate whose type has a destruction-stage receptor, loaded before the
  /// truncate. With no destruction-stage receptor registered, the close range is not even listed.
  /// </summary>
  private async Task<IReadOnlyList<Whizbang.Core.Observability.MessageEnvelope<IEvent>>> _loadDestructionStageEnvelopesAsync(
      IServiceProvider services, Guid streamId, long throughVersion, CancellationToken cancellationToken) {
    var stageTypes = DestructionStageReceptors.TypesWithReceptors(services);
    if (stageTypes.Count == 0) {
      return [];
    }
    var targets = await _coordinator.GetStreamCloseTargetsAsync(streamId, throughVersion, cancellationToken).ConfigureAwait(false);
    return await DestructionStageReceptors.LoadAsync(services, targets, stageTypes, cancellationToken).ConfigureAwait(false);
  }

  [LoggerMessage(EventId = 44, Level = LogLevel.Error,
    Message = "A PreDestructionInline receptor threw for stream {StreamId}; close aborted (detail not truncated)")]
  static partial void LogPreStageFailed(ILogger logger, Exception ex, Guid streamId);

  [LoggerMessage(EventId = 45, Level = LogLevel.Warning,
    Message = "A PostDestructionInline receptor threw for stream {StreamId}; close already committed")]
  static partial void LogPostStageFailed(ILogger logger, Exception ex, Guid streamId);

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

  /// <summary>
  /// Runs the PreDestruction hook and returns the veto result when the hook cancels or defers the
  /// close; null means the close proceeds. A throwing hook aborts the close (rethrown after logging).
  /// </summary>
  private async Task<StreamCloseResult?> _vetoedByPreDestructionAsync(DestructionContext context, Guid streamId, CancellationToken cancellationToken) {
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
      LogCloseVetoed(_logger, streamId, "canceled");
      return new StreamCloseResult("canceled", 0);
    }
    if (decision.DeferUntil.HasValue) {
      LogCloseVetoed(_logger, streamId, "deferred");
      return new StreamCloseResult("deferred", 0);
    }
    return null;
  }
}
