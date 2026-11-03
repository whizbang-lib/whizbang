// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// The maintenance step that disposes of sealed streams once their grace has passed (#1734, WS-3 S5), routing on the
/// disposition (#1735, WS-3 S6), and that carries out the end-of-life policies of each stream type (#1740, WS-3 S11).
/// </summary>
/// <remarks>
/// <para>
/// Each run first schedules up to <see cref="StreamLifecycleOptions.SweepBatchSize"/> sealed streams that have no
/// disposition yet, due <see cref="StreamLifecycleOptions.FinalizeGrace"/> after the later of the seal and now. It then
/// takes up to the same number of due streams, oldest first. A stream a perspective still owes events, or that an active
/// destruction hold covers, is deferred for <see cref="MaintenanceWorkerOptions.DestructionRetryBackoffSeconds"/>.
/// </para>
/// <para>
/// A ready stream gets its final snapshot first: every perspective with a cursor on the stream that this host can run
/// snapshots its row at the cursor's event (<see cref="IPerspectiveRunner.BootstrapSnapshotAsync"/>), which is where an
/// <see cref="AppendToArchivedPolicy.NewGeneration"/> append resumes it. Then the <see cref="IDestructionHook"/> decides:
/// a cancel defers for the backoff, a defer moves the due time to its instant, a throw defers. Otherwise the disposition
/// it returns runs, or the stream type's <see cref="StreamTypeLifecycleOptions.Action"/> (Archive by default) when it
/// names none: Archive writes the whole stream through the <see cref="IArchiveStore"/>; Delete purges it
/// (<see cref="IStreamPurger"/>); Compact runs the registered <see cref="ICompactionHandler"/> and then purges;
/// CryptoShred, and Compact with no handler, defer and log why. A disposition that ran clears the schedule and fires the
/// PostDestruction hook; one that did not defers.
/// </para>
/// <para>
/// When <see cref="StreamLifecycleOptions.Enabled"/> is set and a stream type declares a trigger, the run then asks the
/// ledger which open streams a trigger picks (<see cref="IStreamDispositionLedger.GetPolicyCandidatesAsync"/>). A stream
/// picked whole (idle, age, tenant byte cap) goes the way a due sealed stream does, with the trigger's reason; a purge or
/// a compaction first seals it, only if nothing was appended since it was picked. A stream over its version cap has its
/// prefix cut by the <see cref="IStreamPrefixTruncator"/>, and the cut is recorded.
/// </para>
/// <para>
/// It runs in the maintenance cycle, so under the maintainer duty when role assignment manages it. One stream's failure
/// is logged and deferred, and never holds back the next stream.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/stream-end-of-life#scheduled-disposition</docs>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/StreamDispositionStepTests.cs</tests>
public sealed partial class StreamDispositionStep : IMaintenanceStep {
  /// <summary>The step's name in logs.</summary>
  public const string STEP_NAME = "stream-disposition";

  private static readonly IReadOnlyDictionary<Guid, StreamDispositionReadiness> _noReadiness = new Dictionary<Guid, StreamDispositionReadiness>();

  private readonly TimeProvider _timeProvider;
  private readonly ILogger _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="timeProvider">The clock due times are read and written from.</param>
  /// <param name="logger">Logs each stream's outcome.</param>
  public StreamDispositionStep(TimeProvider timeProvider, ILogger<StreamDispositionStep> logger) {
    ArgumentNullException.ThrowIfNull(timeProvider);
    ArgumentNullException.ThrowIfNull(logger);
    _timeProvider = timeProvider;
    _logger = logger;
  }

  /// <summary>
  /// Builds the step from a host's services, as both drivers register it: on the host's <see cref="TimeProvider"/> when
  /// one is registered and the system clock otherwise, logging to the host's logger when it has logging.
  /// </summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  public static StreamDispositionStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new StreamDispositionStep(
      services.GetService<TimeProvider>() ?? TimeProvider.System,
      services.GetService<ILogger<StreamDispositionStep>>() ?? NullLogger<StreamDispositionStep>.Instance);
  }

  /// <inheritdoc />
  public string Name => STEP_NAME;

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var run = new Run(
      services,
      services.GetRequiredService<IStreamDispositionLedger>(),
      services.GetRequiredService<IOptions<StreamLifecycleOptions>>().Value,
      _timeProvider.GetUtcNow(),
      TimeSpan.FromSeconds(services.GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value.DestructionRetryBackoffSeconds));
    _ = await run.Ledger.ScheduleAsync(run.Lifecycle.FinalizeGrace, run.Lifecycle.SweepBatchSize, run.Now, cancellationToken).ConfigureAwait(false);
    foreach (var due in await run.Ledger.GetDueAsync(run.Now, run.Lifecycle.SweepBatchSize, cancellationToken).ConfigureAwait(false)) {
      await _guardedAsync(run, due.StreamId, () => _disposeAsync(run, due, cancellationToken), cancellationToken).ConfigureAwait(false);
    }
    if (run.Lifecycle.Enabled && run.Lifecycle.EndOfLifePolicies() is { Count: > 0 } policies) {
      await _applyPoliciesAsync(run, policies, cancellationToken).ConfigureAwait(false);
    }
  }

  // What one run reads once and every stream shares.
  private sealed record Run(
      IServiceProvider Services, IStreamDispositionLedger Ledger, StreamLifecycleOptions Lifecycle, DateTimeOffset Now, TimeSpan Backoff) {
    public DateTimeOffset RetryAt => Now + Backoff;
  }

  // A stream an end-of-life policy picked open: it is sealed before a purge or a compaction, only at the version seen.
  private sealed record OpenStreamGuard(long LastVersion, StreamEndOfLifeTrigger Trigger);

  // One stream's work: a failure is logged and the stream deferred for the backoff; a shutdown propagates.
  private async Task _guardedAsync(Run run, Guid streamId, Func<Task> work, CancellationToken ct) {
    try {
      await work().ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      LogDispositionThrew(_logger, streamId, ex);
      await run.Ledger.SetDueAsync(streamId, run.RetryAt, ct).ConfigureAwait(false);
    }
  }

  // A due sealed stream: cleared when it is done already, deferred while it cannot go, otherwise destroyed by its type's
  // declared disposition.
  private async Task _disposeAsync(Run run, DueStreamDisposition due, CancellationToken ct) {
    if (due.Readiness == StreamDispositionReadiness.Done) {
      await run.Ledger.SetDueAsync(due.StreamId, null, ct).ConfigureAwait(false);
      return;
    }
    var context = new DestructionContext {
      Reason = DestructionReason.FinalizeGraceElapsed,
      Granularity = DestructionGranularity.Stream,
      StreamId = due.StreamId,
      DeclaredDefault = run.Lifecycle.ActionFor(due.StreamType).ToDisposition(),
    };
    await _destroyAsync(run, context, due.Readiness, guard: null, ct).ConfigureAwait(false);
  }

  // The end-of-life policies: the streams a trigger picks, whole or by their prefix.
  private async Task _applyPoliciesAsync(Run run, IReadOnlyList<StreamEndOfLifePolicy> policies, CancellationToken ct) {
    var candidates = await run.Ledger.GetPolicyCandidatesAsync(policies, run.Now, run.Lifecycle.SweepBatchSize, ct).ConfigureAwait(false);
    // Readiness gates a destruction; closing the books destroys nothing, so a stream whose type closes them is not asked.
    var whole = candidates
      .Where(c => c.Trigger != StreamEndOfLifeTrigger.VersionCap && run.Lifecycle.ActionFor(c.StreamType) != StreamLifecycleAction.CloseBooks)
      .Select(c => c.StreamId)
      .ToList();
    var readiness = whole.Count == 0 ? _noReadiness : await run.Ledger.GetReadinessAsync(whole, ct).ConfigureAwait(false);
    foreach (var candidate in candidates) {
      await _guardedAsync(run, candidate.StreamId, () => _endOfLifeAsync(run, candidate, readiness, ct), ct).ConfigureAwait(false);
    }
  }

  // One picked stream: its books closed when its type says so (#1741), else its prefix cut or the whole stream ended.
  private Task _endOfLifeAsync(Run run, StreamPolicyCandidate candidate, IReadOnlyDictionary<Guid, StreamDispositionReadiness> readiness, CancellationToken ct) {
    if (run.Lifecycle.ActionFor(candidate.StreamType) == StreamLifecycleAction.CloseBooks) {
      return _closeBooksAsync(run, candidate, ct);
    }
    return candidate.Trigger == StreamEndOfLifeTrigger.VersionCap
      ? _cutPrefixAsync(run, candidate, ct)
      : _endWholeAsync(run, candidate, readiness.GetValueOrDefault(candidate.StreamId, StreamDispositionReadiness.Blocked), ct);
  }

  // Closing the books by policy (#1741): the developer's handler supplies the closing summary and the opening balance, and
  // the closer appends the summary, seals the stream and opens its successor in one transaction. A handler not ready
  // defers the stream; a close the closer refuses (appended meanwhile, sealed, archived) is logged, and the next cycle
  // decides afresh.
  private async Task _closeBooksAsync(Run run, StreamPolicyCandidate candidate, CancellationToken ct) {
    var handler = run.Services.GetService<IBookClosingHandler>();
    var closer = run.Services.GetService<IBookCloser>();
    if (handler is null || closer is null) {
      LogCannotCloseBooks(_logger, candidate.StreamId, handler is null ? nameof(IBookClosingHandler) : nameof(IBookCloser), run.RetryAt);
      await run.Ledger.SetDueAsync(candidate.StreamId, run.RetryAt, ct).ConfigureAwait(false);
      return;
    }
    var books = await handler.PrepareAsync(
      new BookClosingContext(candidate.StreamId, candidate.StreamType, _reasonOf(candidate.Trigger), candidate.LastVersion), ct).ConfigureAwait(false);
    if (books is null) {
      LogBooksNotPrepared(_logger, candidate.StreamId, run.RetryAt);
      await run.Ledger.SetDueAsync(candidate.StreamId, run.RetryAt, ct).ConfigureAwait(false);
      return;
    }
    var result = await books.CloseWithAsync(closer, candidate.StreamId, ct).ConfigureAwait(false);
    LogBooksClosed(_logger, candidate.StreamId, result.Status, result.SuccessorStreamId);
  }

  // A stream a whole-stream trigger picked: the trigger's reason, the type's action, and the seal guard.
  private Task _endWholeAsync(Run run, StreamPolicyCandidate candidate, StreamDispositionReadiness readiness, CancellationToken ct) {
    var context = new DestructionContext {
      Reason = _reasonOf(candidate.Trigger),
      Granularity = DestructionGranularity.Stream,
      StreamId = candidate.StreamId,
      DeclaredDefault = run.Lifecycle.ActionFor(candidate.StreamType).ToDisposition(),
    };
    return _destroyAsync(run, context, readiness, new OpenStreamGuard(candidate.LastVersion, candidate.Trigger), ct);
  }

  // A stream over its version cap: the truncator cuts the prefix (its own gates, hook and compaction), and a cut that
  // happened is recorded so the next cycle counts only the versions above it.
  private async Task _cutPrefixAsync(Run run, StreamPolicyCandidate candidate, CancellationToken ct) {
    if (run.Services.GetService<IStreamPrefixTruncator>() is not { } truncator) {
      LogNoTruncator(_logger, candidate.StreamId, run.RetryAt);
      await run.Ledger.SetDueAsync(candidate.StreamId, run.RetryAt, ct).ConfigureAwait(false);
      return;
    }
    var through = candidate.ThroughVersion!.Value;
    var result = await truncator.TruncateAsync(
      candidate.StreamId, through, run.Lifecycle.ActionFor(candidate.StreamType).ToDisposition(), DestructionReason.VersionCapExceeded, ct)
      .ConfigureAwait(false);
    if (string.Equals(result.Status, "truncated", StringComparison.Ordinal)) {
      await run.Ledger.SetTruncatedThroughAsync(candidate.StreamId, through, ct).ConfigureAwait(false);
      return;
    }
    LogPrefixNotCut(_logger, candidate.StreamId, through, result.Status, run.RetryAt);
    await run.Ledger.SetDueAsync(candidate.StreamId, run.RetryAt, ct).ConfigureAwait(false);
  }

  // Destroys one stream: deferred while it cannot go; otherwise its final snapshot, the PreDestruction hook, the seal
  // guard of a stream picked open, the disposition, and the PostDestruction hook. The schedule is cleared before the
  // PostDestruction hook, so a slow or failing notification never leaves a disposed stream scheduled.
  private async Task _destroyAsync(Run run, DestructionContext context, StreamDispositionReadiness readiness, OpenStreamGuard? guard, CancellationToken ct) {
    var streamId = context.StreamId!.Value;
    if (readiness != StreamDispositionReadiness.Ready) {
      LogNotReady(_logger, streamId, readiness, run.RetryAt);
      await run.Ledger.SetDueAsync(streamId, run.RetryAt, ct).ConfigureAwait(false);
      return;
    }
    await _takeFinalSnapshotsAsync(run.Services, run.Ledger, streamId, ct).ConfigureAwait(false);

    var hook = run.Services.GetService<IDestructionHook>();
    var decision = hook is null
      ? DestructionResult.Proceed()
      : await hook.OnBeforeDestructionAsync(context, ct).ConfigureAwait(false);
    if (decision.Cancel || decision.DeferUntil.HasValue) {
      var until = decision.DeferUntil ?? run.RetryAt;
      LogVetoed(_logger, streamId, until);
      await run.Ledger.SetDueAsync(streamId, until, ct).ConfigureAwait(false);
      return;
    }
    var disposition = decision.Disposition ?? context.DeclaredDefault;
    if (guard is not null && !await _sealIfStillUnchangedAsync(run, streamId, disposition, guard, ct).ConfigureAwait(false)) {
      return;
    }
    if (!await _runAsync(run.Services, disposition, context, ct).ConfigureAwait(false)) {
      await run.Ledger.SetDueAsync(streamId, run.RetryAt, ct).ConfigureAwait(false);
      return;
    }
    await run.Ledger.SetDueAsync(streamId, null, ct).ConfigureAwait(false);
    if (hook is not null) {
      await _announceAsync(hook, context, ct).ConfigureAwait(false);
    }
  }

  // PostDestruction: a failure is logged and changes nothing, because the disposition already ran.
  private async Task _announceAsync(IDestructionHook hook, DestructionContext context, CancellationToken ct) {
    try {
      await hook.OnAfterDestructionAsync(context, ct).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      LogPostHookThrew(_logger, context.StreamId!.Value, ex);
    }
  }

  // A purge, or a compaction that will run, of a stream picked open seals it first, and only when nothing was appended
  // since it was picked: an append in between is stored before the seal (which is then refused) or refused by it, so it is
  // never purged unseen. An archive is reversible and its stream may be appended to again under its AppendToArchived
  // policy, so it is not sealed. True when the disposition may run.
  private async Task<bool> _sealIfStillUnchangedAsync(Run run, Guid streamId, Disposition disposition, OpenStreamGuard guard, CancellationToken ct) {
    var destroysDetail = disposition == Disposition.Delete
      || (disposition == Disposition.Compact && run.Services.GetService<ICompactionHandler>() is not null);
    if (!destroysDetail) {
      return true;
    }
    var reason = $"End-of-life policy ({guard.Trigger}): the stream is {(disposition == Disposition.Delete ? "purged" : "compacted")}";
    if (await run.Ledger.SealIfUnchangedAsync(streamId, guard.LastVersion, reason, run.Now, ct).ConfigureAwait(false)) {
      return true;
    }
    LogAppendedSincePicked(_logger, streamId, guard.Trigger);
    return false;
  }

  private static DestructionReason _reasonOf(StreamEndOfLifeTrigger trigger) => trigger switch {
    StreamEndOfLifeTrigger.MaxAge => DestructionReason.MaxAgeReached,
    StreamEndOfLifeTrigger.TenantByteCap => DestructionReason.TenantByteCapExceeded,
    StreamEndOfLifeTrigger.VersionCap => DestructionReason.VersionCapExceeded,
    _ => DestructionReason.IdleTtlElapsed,
  };

  // The final snapshot: each perspective with a cursor on the stream that this host can run snapshots its row at the
  // cursor's event. A perspective this host has no runner for, or a cursor at no event, is skipped; a NewGeneration
  // append to the stream then reads as AutoRestore, which loses nothing.
  private static async Task _takeFinalSnapshotsAsync(IServiceProvider services, IStreamDispositionLedger ledger, Guid streamId, CancellationToken ct) {
    var registry = services.GetService<IPerspectiveRunnerRegistry>();
    if (registry is null) {
      return;
    }
    foreach (var cursor in await ledger.GetCursorsAsync(streamId, ct).ConfigureAwait(false)) {
      if (cursor.LastEventId is not { } eventId || registry.GetRunner(cursor.PerspectiveName, services) is not { } runner) {
        continue;
      }
      await runner.BootstrapSnapshotAsync(streamId, cursor.PerspectiveName, eventId, ct).ConfigureAwait(false);
    }
  }

  // Runs the disposition; true when it ran.
  private async Task<bool> _runAsync(IServiceProvider services, Disposition disposition, DestructionContext context, CancellationToken ct) {
    var streamId = context.StreamId!.Value;
    switch (disposition) {
      case Disposition.Archive: {
          var written = await services.GetRequiredService<IArchiveStore>()
            .WriteAsync(ArchiveBatch.WholeStream(streamId), ct).ConfigureAwait(false);
          if (!written.Archived) {
            LogArchiveRefused(_logger, streamId, written.Status);
          }
          return written.Archived;
        }
      case Disposition.Delete:
        await _purgeAsync(services, streamId, context.Reason, ct).ConfigureAwait(false);
        return true;
      case Disposition.Compact when services.GetService<ICompactionHandler>() is { } compactor:
        await compactor.CompactAsync(context, ct).ConfigureAwait(false);
        await _purgeAsync(services, streamId, context.Reason, ct).ConfigureAwait(false);
        return true;
      default:
        LogUnavailable(_logger, streamId, disposition,
          disposition == Disposition.Compact ? "it needs an ICompactionHandler registered, and none is" : "it has not shipped");
        return false;
    }
  }

  private static Task<StreamPurgeReport> _purgeAsync(IServiceProvider services, Guid streamId, DestructionReason reason, CancellationToken ct) =>
    services.GetRequiredService<IStreamPurger>().PurgeAsync(new StreamPurgeRequest {
      StreamIds = [streamId],
      RequestedBy = STEP_NAME,
      Reason = _purgeReason(reason),
    }, ct);

  private static string _purgeReason(DestructionReason reason) => reason switch {
    DestructionReason.IdleTtlElapsed => "Nothing was appended to the stream for its type's IdleTtl, and its end-of-life action purges it",
    DestructionReason.MaxAgeReached => "The stream's first append is older than its type's MaxAge, and its end-of-life action purges it",
    DestructionReason.TenantByteCapExceeded => "The tenant's streams of the type exceed its TenantByteCap, and its end-of-life action purges it",
    _ => "The sealed stream's FinalizeGrace passed and its disposition purges it",
  };

  [LoggerMessage(Level = LogLevel.Debug, Message = "Disposition of stream {StreamId} deferred until {RetryAt}: {Readiness}")]
  static partial void LogNotReady(ILogger logger, Guid streamId, StreamDispositionReadiness readiness, DateTimeOffset retryAt);

  [LoggerMessage(Level = LogLevel.Information, Message = "Disposition of stream {StreamId} deferred by the PreDestruction hook until {Until}")]
  static partial void LogVetoed(ILogger logger, Guid streamId, DateTimeOffset until);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Archiving stream {StreamId} was refused ({Status}); its disposition is deferred")]
  static partial void LogArchiveRefused(ILogger logger, Guid streamId, string status);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Stream {StreamId} kept: its disposition is {Disposition}, which cannot run: {Reason}")]
  static partial void LogUnavailable(ILogger logger, Guid streamId, Disposition disposition, string reason);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Disposition of stream {StreamId} failed and is deferred")]
  static partial void LogDispositionThrew(ILogger logger, Guid streamId, Exception exception);

  [LoggerMessage(Level = LogLevel.Warning, Message = "PostDestruction hook threw for stream {StreamId}; its disposition already ran")]
  static partial void LogPostHookThrew(ILogger logger, Guid streamId, Exception exception);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Stream {StreamId} is due to close its books, but no {Missing} is registered; deferred until {RetryAt}")]
  static partial void LogCannotCloseBooks(ILogger logger, Guid streamId, string missing, DateTimeOffset retryAt);

  [LoggerMessage(Level = LogLevel.Debug, Message = "The IBookClosingHandler is not ready to close the books of stream {StreamId}; deferred until {RetryAt}")]
  static partial void LogBooksNotPrepared(ILogger logger, Guid streamId, DateTimeOffset retryAt);

  [LoggerMessage(Level = LogLevel.Information, Message = "Closing the books of stream {StreamId}: {Status}, successor {SuccessorStreamId}")]
  static partial void LogBooksClosed(ILogger logger, Guid streamId, BookCloseStatus status, Guid? successorStreamId);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Stream {StreamId} picked by its {Trigger} trigger was appended to since; it is left open")]
  static partial void LogAppendedSincePicked(ILogger logger, Guid streamId, StreamEndOfLifeTrigger trigger);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Stream {StreamId} is over its version cap, but no IStreamPrefixTruncator is registered; deferred until {RetryAt}")]
  static partial void LogNoTruncator(ILogger logger, Guid streamId, DateTimeOffset retryAt);

  [LoggerMessage(Level = LogLevel.Information, Message = "Stream {StreamId} is over its version cap; the cut through {ThroughVersion} did not happen ({Status}) and is deferred until {RetryAt}")]
  static partial void LogPrefixNotCut(ILogger logger, Guid streamId, long throughVersion, string status, DateTimeOffset retryAt);
}
