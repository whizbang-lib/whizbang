// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Workers;

/// <summary>
/// The drain path's per-(stream, perspective) work, split into the phases a set-based batch runs them in (#2246): begin
/// (resolve the runner and cursor, set aside cooled and covered events, find an inversion), apply (a rewind, the
/// per-pair runner, or the batch), and finish (report, signal sync, enqueue completions). The per-pair path runs the
/// three in a row under one lease; the set-based path runs the first lap of every stream of a drain batch phase by
/// phase, and each stream's later laps per pair.
/// </summary>
public partial class PerspectiveWorker {
  /// <summary>One (stream, perspective) of a drain lap between its phases.</summary>
  private sealed class DrainPair(
      Guid streamId, string perspectiveName, DrainBatchContext batchContext, DrainGroupScopeLifetime scopeLifetime,
      AsyncServiceScope scope) {
    public Guid StreamId { get; } = streamId;
    public string PerspectiveName { get; } = perspectiveName;
    public DrainBatchContext BatchContext { get; } = batchContext;
    public DrainGroupScopeLifetime ScopeLifetime { get; } = scopeLifetime;
    public AsyncServiceScope Scope { get; } = scope;
    public IWorkCoordinator WorkCoordinator { get; init; } = null!;
    public IPerspectiveRunner Runner { get; set; } = null!;
    public Guid? LastProcessedEventId { get; set; }
    public long? LastProcessedCommitSequence { get; set; }
    public List<MessageEnvelope<IEvent>> FilteredEvents { get; set; } = [];
    public int CooledCount { get; set; }
    public Guid? InversionAnchor { get; set; }
    public long StartTicks { get; set; }

    /// <summary>The pair this drain pair applies in a set-based batch.</summary>
    public PerspectiveApplyBatch.Pair? BatchPair { get; set; }
  }

  /// <summary>The events of a drain lap that one perspective applies, in id order (the collective sink takes the collective events).</summary>
  private List<MessageEnvelope<IEvent>> _drainEventsFor(
      string perspectiveName, List<MessageEnvelope<IEvent>> events, DrainBatchContext context) =>
    // The __collective__ sink has no registered IPerspectiveFor, so it can't be matched via
    // _perspectivesPerEventType — select the collective events directly. _processCollectiveSinkAsync
    // reloads them from the event store, but the list must be non-empty to clear the empty-skip gate.
    perspectiveName == CollectiveRouting.SINK_PERSPECTIVE_NAME
      ? events.Where(e => e.Payload is ICollectiveEvent).OrderByMessageId().ToList()
      : [.. events
          .Where(e => _perspectivesFor(context.TypeNameCache, e.Payload).Contains(perspectiveName))
          .OrderByMessageId()];

  /// <summary>One lap of a stream's drain, per pair: each perspective of the lap's events under its own affinity gate.</summary>
  private async Task _drainLapAsync(
      Guid streamId, List<MessageEnvelope<IEvent>> currentEvents, DrainBatchContext currentContext, CancellationToken ct) {
    var perspectiveNames = _collectDrainModePerspectiveNames(currentEvents, currentContext.TypeNameCache);
    _registerDrainModeLifecycleTracking(currentEvents, currentContext.TypeNameCache, streamId, currentContext.LifecycleCoordinator);
    foreach (var perspectiveName in perspectiveNames) {
      var filteredEvents = _drainEventsFor(perspectiveName, currentEvents, currentContext);
      if (filteredEvents.Count == 0) {
        continue;
      }
      await _drainPerspectiveGatedAsync(streamId, perspectiveName, filteredEvents, currentContext, ct).ConfigureAwait(false);
    }
  }

  /// <summary>One perspective of a drain lap on the per-pair path, under its affinity gate.</summary>
  private async Task _drainPerspectiveGatedAsync(
      Guid streamId, string perspectiveName, List<MessageEnvelope<IEvent>> filteredEvents, DrainBatchContext context,
      CancellationToken ct) {
    // Slice 30 defensive: an OCE bubbling out of the drain-mode perspective method
    // — for example shutdown cancellation with both ct + lease.Token canceled so its
    // own catch filter does not match — must not abort the loop for other perspectives
    // on this stream. The single perspective's lease-handle catch already routes
    // failure-mode OCEs; anything that bubbles is either expected shutdown propagation
    // or an unhandled edge case, neither of which should poison sibling perspectives.
    try {
      // Intra-pod stream-affinity gate — serialize apply for this (stream, perspective) across
      // the parallel consumer loops AND against the standard path, closing the same-pod
      // double-apply window the standard path already gated. See WithStreamAffinityGateAsync.
      await WithStreamAffinityGateAsync(streamId, perspectiveName, keepGateHeldUntil =>
        _runDrainModePerspectiveAsync(streamId, perspectiveName, filteredEvents, context, keepGateHeldUntil, ct), ct).ConfigureAwait(false);
    } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
      // Worker shutdown — propagate out of the loop, Parallel.ForEachAsync handles it.
      throw;
    } catch (OperationCanceledException ex) {
      // Lease-handle OCE that wasn't caught by _runDrainModePerspectiveAsync's filter.
      // Log + continue with next perspective; the row's lease will expire and
      // claim_orphaned will re-issue.
#pragma warning disable CA1848
      _logger.LogWarning(ex, "Drain mode: unhandled OCE from {Perspective} on stream {StreamId} — skipping to next perspective", perspectiveName, streamId);
#pragma warning restore CA1848
    }
  }

  /// <summary>
  /// Runs a single perspective's RunWithEventsAsync for a stream, reports completion/failure,
  /// and flushes the supporting trackers/signalers/metrics. Keeps the pre-report and post-report
  /// completed-only blocks separate to preserve the observable ordering of the original monolith.
  /// </summary>
  /// <param name="keepGateHeldUntil">Hands the affinity gate an apply the lease executor stopped waiting
  /// for while it is still running, so the gate stays held until that apply ends.</param>
  private async Task _runDrainModePerspectiveAsync(
      Guid streamId,
      string perspectiveName,
      List<MessageEnvelope<IEvent>> filteredEvents,
      DrainBatchContext batchContext,
      Action<Task> keepGateHeldUntil,
      CancellationToken ct) {
    var pair = await _beginDrainModePerspectiveAsync(streamId, perspectiveName, filteredEvents, batchContext, ct).ConfigureAwait(false);
    if (pair is null) {
      return;
    }
    // Disposes the scope when the group ends, unless an apply the lease executor abandoned is still
    // running on it: that apply's runner, coordinator and DbContext (with a command possibly in flight)
    // live in this scope, so it is then disposed after the apply ends (#1382).
    await using var groupScopeLifetime = pair.ScopeLifetime;

    // Phase H step 9 slice 4: lease-tied cancellation. Wrap the apply + post-apply housekeeping
    // in a LeaseHandle whose token cancels at lease_expiry - LeaseGraceSeconds. The runner
    // template's apply loop now also calls ThrowIfCancellationRequested between events so a
    // hot stream with many pending events can be canceled mid-batch. The DispatchExecutor
    // abandons hung receptors that ignore the CT.
    using var lease = _drainLease(streamId, ct);

    pair.StartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    _markAffinityPhase(streamId, perspectiveName, "apply");
    // The apply as a task of its own: when the lease runs out (or the worker stops) the executor stops
    // waiting for it, but it keeps running, so the finally below hands it to the affinity gate.
    var apply = Task.CompletedTask;
    try {
      await LeaseDispatchExecutor.RunWithLeaseAsync(lease, leaseCt => apply = applyUnderLeaseAsync(leaseCt)).ConfigureAwait(false);

      async Task applyUnderLeaseAsync(CancellationToken leaseCt) {
        var result = pair.InversionAnchor.HasValue
          ? await _rewindDrainPairAsync(pair, leaseCt).ConfigureAwait(false)
          : await pair.Runner.RunWithEventsAsync(
              streamId, perspectiveName, pair.LastProcessedEventId, pair.FilteredEvents, leaseCt).ConfigureAwait(false);
        await _finishDrainModePerspectiveAsync(pair, result, leaseCt).ConfigureAwait(false);
      }
    } catch (OperationCanceledException leaseExpired) when (lease.Token.IsCancellationRequested && !ct.IsCancellationRequested) {
      await _failDrainPairOnLeaseExpiryAsync(pair, leaseExpired, ct).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      await _failDrainPairAsync(pair, ex, ct).ConfigureAwait(false);
    } finally {
      // An apply the executor abandoned is still running and has not marked its events processed. The
      // gate stays held until it ends, so no other consumer can apply the same events in the meantime,
      // and the group's scope, which the apply is still using, is disposed only after it ends.
      if (!apply.IsCompleted) {
        keepGateHeldUntil(groupScopeLifetime.DisposeAfterAsync(apply));
      }
    }
  }

  /// <summary>
  /// A drain pair's lease: its token cancels at lease expiry less the grace, so an apply that outlives its lease is
  /// routed to the failure path and its rows are re-issued.
  /// </summary>
  private LeaseHandle _drainLease(Guid streamId, CancellationToken ct) {
    var leaseDeadline = _timeProvider.GetUtcNow()
      + TimeSpan.FromSeconds(Math.Max(1, _leaseRenewalOptions.LeaseSeconds - _leaseHandleOptions.LeaseGraceSeconds));
    return new LeaseHandle(
      workId: streamId,
      category: WorkCategory.PerspectiveEvent,
      deadline: leaseDeadline,
      maxRenewals: _leaseHandleOptions.MaxRenewalsPerWork,
      timeProvider: _timeProvider,
      linkedTokens: [ct]);
  }

  /// <summary>
  /// Begins a drain pair: its scope, the collective sink (run here in full), the runner and cursor, the cooled and
  /// covered events (signaled and set aside), and an inversion. Null when the pair has ended here.
  /// </summary>
  private async Task<DrainPair?> _beginDrainModePerspectiveAsync(
      Guid streamId,
      string perspectiveName,
      List<MessageEnvelope<IEvent>> filteredEvents,
      DrainBatchContext batchContext,
      CancellationToken ct) {
    var groupScope = _scopeFactory.CreateAsyncScope();
    var groupScopeLifetime = new DrainGroupScopeLifetime(groupScope, _logger, streamId, perspectiveName);
    var pair = new DrainPair(streamId, perspectiveName, batchContext, groupScopeLifetime, groupScope) {
      WorkCoordinator = groupScope.ServiceProvider.GetRequiredService<IWorkCoordinator>(),
    };
    var begun = false;
    try {
      _markAffinityPhase(streamId, perspectiveName, "resolve");

      // Collective-event sink (drain path twin of the channel-path guard): dispatch via
      // ICollectiveDispatcher and skip the per-stream runner. See _processCollectiveSinkAsync.
      if (perspectiveName == CollectiveRouting.SINK_PERSPECTIVE_NAME) {
        // Resolve the sink rows' event_work_ids from this drain batch's raw rows so the sink can complete
        // (DELETE) them on success — without it the row loops forever (a production re-dispatch death spiral).
        var sinkWorkIds = filteredEvents
          .SelectMany(e => batchContext.RawByEventId[e.MessageId.Value])
          .Where(raw => string.Equals(raw.PerspectiveName, CollectiveRouting.SINK_PERSPECTIVE_NAME, StringComparison.Ordinal))
          .Select(raw => raw.EventWorkId)
          .Where(id => id != Guid.Empty)
          .Distinct()
          .ToArray();
        _markAffinityPhase(streamId, perspectiveName, "collective");
        await _processCollectiveSinkAsync(groupScope, pair.WorkCoordinator, streamId, sinkWorkIds, ct).ConfigureAwait(false);
        batchContext.AppliedGroups.TryAdd((streamId, perspectiveName), 0);
        return null;
      }

      var registry = groupScope.ServiceProvider.GetService<IPerspectiveRunnerRegistry>();
      var runner = registry?.GetRunner(perspectiveName, groupScope.ServiceProvider);
      if (runner is null) {
        return null;
      }
      pair.Runner = runner;

      // Cold-cache fix: when the cursor cache is cold (process start,
      // post-rewind invalidate, eviction), TryGet returns false and the
      // inversion detector below sees null cursors. Late events would then slip
      // past detection into the runner's forward-apply path, where the runner's
      // own idempotency filter (which DOES see the persisted metadata.EventId)
      // silently drops them — the "5 lost events, 0 rewinds" symptom seen in a consumer deployment.
      //
      // Cold-cache fallback: read persisted wh_perspective_cursors via the
      // already-scoped IWorkCoordinator and warm the cache. Subsequent
      // in-process readers (incl. PerspectiveCursorCache.TryGet callers
      // elsewhere) now see the same truth source the runner filter uses.
      // Contract is also locked at PerspectiveCursorResolver for any other
      // component that needs the same cold-fallback semantics.
      var eventIdInCache = _cursorCache.TryGet(streamId, perspectiveName, out var cachedEventId);
      var commitSeqInCache = _cursorCache.TryGetCommitSequence(streamId, perspectiveName, out var cachedCommitSeq);
      if (eventIdInCache && commitSeqInCache) {
        pair.LastProcessedEventId = cachedEventId;
        pair.LastProcessedCommitSequence = cachedCommitSeq;
      } else if (!eventIdInCache && batchContext.CursorsReadFor.Contains(streamId)) {
        // The batch read this stream's cursors and found none for the pair: a new pair has no cursor to read (#2246).
        pair.LastProcessedEventId = null;
        pair.LastProcessedCommitSequence = null;
      } else {
        var persisted = await pair.WorkCoordinator
          .GetPerspectiveCursorAsync(streamId, perspectiveName, ct)
          .ConfigureAwait(false);
        pair.LastProcessedEventId = persisted?.LastEventId;
        pair.LastProcessedCommitSequence = persisted?.LastCommitSequence;
        if (persisted is not null) {
          // Warm the cache so subsequent calls in this and other code paths see
          // the same cursor without paying for another round-trip.
          _cursorCache.Set(streamId, perspectiveName, persisted.LastEventId);
          _cursorCache.SetCommitSequence(streamId, perspectiveName, persisted.LastCommitSequence);
        }
      }

      // Slice 26.15: partition cooled vs fresh BEFORE the inversion check. The cursor-flush
      // race between drain finish and PerspectiveCompletionFlushWorker DELETE produces a
      // window where just-applied event_work_ids are still in DB pending. Pre-26.15 the
      // inversion detector ran on the full set, saw those cooled events as "pending ≤ cursor",
      // and triggered a spurious rewind — measured as ~1100 phantom rewinds per consumer bulk-import
      // when sagas commit N events in two close transactions (drainer re-fetches between flush
      // ticks, sees first-batch events still warm, second-batch fresh, mixed bag).
      var (cooledEvents, freshEvents) = _partitionByCooldown(
        filteredEvents, batchContext.RawByEventId, _recentlyProcessedEventCache, perspectiveName);
      pair.CooledCount = cooledEvents.Count;

      // Signal cooled events into the batch's completion bookkeeping so PostAllPerspectives
      // can fire correctly — same reasoning as the all-cooled short-circuit.
      // SignalPerspectiveComplete + BatchProcessedEvents.TryAdd are idempotent so re-signaling
      // is safe even when the previous drain already signaled them.
      if (cooledEvents.Count > 0) {
        _signalCooldownSkippedEvents(
          cooledEvents, perspectiveName, streamId,
          batchContext.BatchProcessedEvents, batchContext.LifecycleCoordinator);
        // Diagnostic: surface cooled event_ids + work_ids so we can correlate against
        // wh_event_store and confirm whether the cooldown decision was correct
        // (event was truly applied previously) or a silent-drop bug (event never applied).
        // Gated on IsEnabled to avoid materializing the id strings when Debug logging is off.
        // CA1873 fires on the string.Join args even with the IsEnabled guard — the analyzer
        // doesn't look through the surrounding conditional for source-generated LoggerMessage.
#pragma warning disable CA1873
        if (_logger.IsEnabled(LogLevel.Debug)) {
          LogDiagnosticCooldown(_logger, streamId, perspectiveName, cooledEvents.Count,
            string.Join(",", cooledEvents.Select(e => e.MessageId.Value.ToString("D"))),
            string.Join(",", cooledEvents.SelectMany(e => batchContext.RawByEventId[e.MessageId.Value])
              .Where(raw => string.Equals(raw.PerspectiveName, perspectiveName, StringComparison.Ordinal))
              .Select(raw => raw.EventWorkId.ToString("D"))));
        }
#pragma warning restore CA1873
      }

      // #1361: an event a completed rewind was already run for is covered, whatever row brought it back: the
      // rewind's own rows still pending before the completion flush deletes them, or the same event delivered again
      // under a new work row. Neither is a new inversion. Their rows are completed, with nothing applied; rewinding again
      // would replay the whole stream once per lap of the drain loop.
      var coveredEvents = _takeReplayedEvents(freshEvents, streamId, perspectiveName);
      if (coveredEvents.Count > 0) {
        _signalCooldownSkippedEvents(
          coveredEvents, perspectiveName, streamId,
          batchContext.BatchProcessedEvents, batchContext.LifecycleCoordinator);
        _markProcessedInCooldown(_recentlyProcessedEventCache, coveredEvents, batchContext.RawByEventId, perspectiveName);
        _enqueueDrainModePerspectiveCompletions(streamId, perspectiveName, coveredEvents, batchContext.RawByEventId);
        LogEventsCoveredByRewind(_logger, streamId, perspectiveName, coveredEvents.Count);
      }

      // Everything cooled or covered → a previous run handled it; nothing left to do.
      if (freshEvents.Count == 0) {
        return null;
      }

      // Phase H step 6 slice 4: cursor-inversion detector — now scoped to the fresh remainder
      // so cursor-flush-race events don't masquerade as real inversions.
      pair.InversionAnchor = _resolveInversionAnchor(
        freshEvents, batchContext.RawByEventId, pair.LastProcessedEventId, pair.LastProcessedCommitSequence);

      // freshEvents is what we'll apply (or rewind around).
      pair.FilteredEvents = freshEvents;
      begun = true;
      return pair;
    } finally {
      if (!begun) {
        await groupScopeLifetime.DisposeAsync().ConfigureAwait(false);
      }
    }
  }

  /// <summary>The rewind a drain pair's inversion asks for, counted and traced like every rewind.</summary>
  private async Task<PerspectiveCursorCompletion> _rewindDrainPairAsync(DrainPair pair, CancellationToken leaseCt) {
    var (streamId, perspectiveName) = (pair.StreamId, pair.PerspectiveName);
    var inversionAnchor = pair.InversionAnchor!.Value;
    // Cache is stale — reset and let RewindAndRunAsync rebuild from snapshot/event-zero.
    _cursorCache.Invalidate(streamId, perspectiveName);
    // Slice 26.11: look up the violator's commit_sequence so RewindAndRunAsync can
    // use the commit-sequence-anchored snapshot path for full determinism. Falls
    // back to event_id-anchored snapshot when the violator wasn't stamped yet.
    long? anchorCommitSequence = pair.BatchContext.RawByEventId[inversionAnchor]
      .Select(raw => raw.CommitSequence)
      .FirstOrDefault(seq => seq.HasValue);
    // Slice 26.16 instrumentation: emit detailed inversion diagnostics so we can
    // classify the residual ~400 inversions per consumer import. Captures whether the
    // commit_sequence detector or the event_id fallback fired, the gap, and the
    // partition counts so we can spot cooldown misses.
    long? pendingSeq = anchorCommitSequence;
    LogInversionDiagnostics(
      _logger,
      streamId,
      perspectiveName,
      inversionAnchor,
      pair.LastProcessedEventId ?? Guid.Empty,
      pendingSeq ?? -1,
      pair.LastProcessedCommitSequence ?? -1,
      pair.CooledCount,
      pair.FilteredEvents.Count);
    LogRewindTriggered(_logger, streamId, perspectiveName, inversionAnchor, pair.LastProcessedEventId ?? Guid.Empty);
    // Through the same helper the rewind work-item path uses, so this rewind is counted and
    // traced like any other. It used to call the runner directly, which is why the rewind
    // meters read zero while these rewinds ran (#1362).
    var result = await _rewindAndCountAsync(
      pair.Runner,
      new RewindRequest(streamId, perspectiveName, inversionAnchor, anchorCommitSequence, EventsBehind: null),
      _tracingOptions.CurrentValue.IsEnabled(TraceComponents.Perspectives),
      leaseCt).ConfigureAwait(false);
    // #1361: the rewind applied this batch's events. A later lap or batch that brings any of them back is
    // covered, not a new inversion.
    if (result.Status == PerspectiveProcessingStatus.Completed) {
      _cursorCache.MarkReplayed(streamId, perspectiveName, pair.FilteredEvents.Select(e => e.MessageId.Value));
    }
    return result;
  }

  /// <summary>What follows a drain pair's apply: stragglers, completion, report, sync, completions and metrics.</summary>
  private async Task _finishDrainModePerspectiveAsync(DrainPair pair, PerspectiveCursorCompletion result, CancellationToken leaseCt) {
    var (streamId, perspectiveName, batchContext) = (pair.StreamId, pair.PerspectiveName, pair.BatchContext);
    var filteredEvents = pair.FilteredEvents;
    // #1862: an ephemeral runner with no snapshot floor drops a late event instead of rewinding for it. What it
    // dropped is retired here, once, and the rest of the batch is handed back; otherwise the event's work stays
    // pending and every lap and claim finds it, skips the same rewind and drops it again.
    if (result.DroppedStragglers.Length > 0) {
      (result, filteredEvents) = await _retireDroppedStragglersAsync(
        pair.Runner, result, filteredEvents,
        new PerspectiveStreamContext(streamId, perspectiveName, pair.LastProcessedEventId, pair.Scope.ServiceProvider),
        batchContext, leaseCt).ConfigureAwait(false);
      pair.FilteredEvents = filteredEvents;
    }
    // The runner ran for this (stream, perspective): the batch's claimed copy of the same
    // group is redundant. Only a real runner invocation records here; a pass that cooled
    // everything returned above and left the group unrecorded on purpose (issue #700).
    batchContext.AppliedGroups.TryAdd((streamId, perspectiveName), 0);
    if (result.Status == PerspectiveProcessingStatus.Completed) {
      _storedFormFailures.Recovered(perspectiveName, streamId);
    }

    // Slice 29 instrumentation: capture per-drain wall time partitioned into the three
    // dominant phases — runner (read + apply + save), completion (cursor update + lifecycle),
    // completion-enqueue + signaler. Surfaces the per-drain cost dominant when a consumer's
    // service drains at ~40 events/sec despite 4×30 concurrency configured.
    var runnerEndTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    var runnerMs = (runnerEndTicks - pair.StartTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    if (result.Status == PerspectiveProcessingStatus.Completed) {
      var streamCtx = new PerspectiveStreamContext(streamId, perspectiveName, pair.LastProcessedEventId, pair.Scope.ServiceProvider);
      _markAffinityPhase(streamId, perspectiveName, "completion");
      await _applyDrainModePerspectiveCompletionAsync(
        streamCtx, filteredEvents, result, batchContext, leaseCt).ConfigureAwait(false);
    }
    var completionMs = (System.Diagnostics.Stopwatch.GetTimestamp() - runnerEndTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    _markAffinityPhase(streamId, perspectiveName, "report");
    await _completionStrategy.ReportCompletionAsync(result, pair.WorkCoordinator, leaseCt).ConfigureAwait(false);

    if (filteredEvents.Count > 0) {
      var processedEventIds = filteredEvents.ConvertAll(e => e.MessageId.Value);
      // Only a committed apply marks the events applied: a waiter for an event from another service
      // reads this as "the read model has it", which a failure must never claim (#959). Marked before
      // the processed release, so anything that observes the release has seen the mark decided.
      if (result.Status == PerspectiveProcessingStatus.Completed) {
        _syncEventTracker.MarkApplied((IEnumerable<Guid>)processedEventIds, perspectiveName);
      }
      _syncEventTracker.MarkProcessedByPerspective((IEnumerable<Guid>)processedEventIds, perspectiveName);
    }

    // Belt-and-suspenders sweep — when the runner short-circuits its batch
    // (already-applied / cooldown / dedup filtered all events) filteredEvents
    // is empty and the per-event MarkProcessedByPerspective above doesn't fire,
    // leaving any waiter that registered against those tracked events stuck
    // on its TCS for the full sync-wait timeout. The stream-level sweep wakes
    // them as soon as the perspective reports Completed for the stream.
    if (result.Status == PerspectiveProcessingStatus.Completed) {
      _syncEventTracker.MarkPerspectiveStreamProcessed(perspectiveName, streamId);
    }

    if (result.PerspectiveType is not null) {
      _syncSignaler.SignalCheckpointUpdated(result.PerspectiveType, streamId, result.LastEventId);
    }

    if (result.Status == PerspectiveProcessingStatus.Completed) {
      _enqueueDrainModePerspectiveCompletions(streamId, perspectiveName, filteredEvents, batchContext.RawByEventId);
      // Slice 26.17: cooldown is now marked at the top of _applyDrainModePerspectiveCompletionAsync,
      // before any cursor cache update or lifecycle invocation that could throw. The
      // late-mark site here was the source of residual inversions in a consumer's
      // bulk run — keep the call site comment as a regression breadcrumb.
    }

    _metrics?.StreamsUpdated.Add(1);
    if (filteredEvents.Count > 0) {
      _metrics?.EventsProcessed.Add(filteredEvents.Count);
    }

    // Slice 29 instrumentation: emit per-drain perf breakdown at Debug when we processed
    // >= 5 events or the drain took more than 100ms. Surfaces whether per-drain time
    // scales with event count (apply is the cost) or is fixed-per-call (lifecycle / DI
    // scope is the cost). Enable Debug logging for the worker to see these.
    if (_logger.IsEnabled(LogLevel.Debug)) {
      var totalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - pair.StartTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
      if (filteredEvents.Count >= 5 || totalMs > 100) {
#pragma warning disable CA1848
        _logger.LogDebug(
          "PERF Drain {Perspective} stream {StreamId}: events={EventCount} total={TotalMs:F0}ms runner={RunnerMs:F0}ms completion={CompletionMs:F0}ms cooled={Cooled}",
          perspectiveName, streamId, filteredEvents.Count, totalMs, runnerMs, completionMs, pair.CooledCount);
#pragma warning restore CA1848
      }
    }
  }

  /// <summary>
  /// A drain pair whose lease ran out (not the worker stopping): routed to the failure path like any other failure,
  /// so the row's lease releases and claim_orphaned re-issues it with its attempts bumped.
  /// </summary>
  private async Task _failDrainPairOnLeaseExpiryAsync(DrainPair pair, OperationCanceledException leaseExpired, CancellationToken ct) {
#pragma warning disable CA1848
    _logger.LogWarning(leaseExpired, "Drain mode: lease deadline exceeded for {Perspective} stream {StreamId} — routing to failure", pair.PerspectiveName, pair.StreamId);
#pragma warning restore CA1848
    _metrics?.Errors.Add(1);
    var failure = new PerspectiveCursorFailure {
      StreamId = pair.StreamId,
      PerspectiveName = pair.PerspectiveName,
      LastEventId = Guid.Empty,
      Status = PerspectiveProcessingStatus.Failed,
      Error = "Lease deadline exceeded — handler did not complete within the configured lease window."
    };
    await _completionStrategy.ReportFailureAsync(failure, pair.WorkCoordinator, ct).ConfigureAwait(false);
  }

  /// <summary>A drain pair whose apply failed: its rows are parked (or the stored form recorded) and the failure reported.</summary>
  private async Task _failDrainPairAsync(DrainPair pair, Exception ex, CancellationToken ct) {
    var (streamId, perspectiveName) = (pair.StreamId, pair.PerspectiveName);
    var leasedRows = _drainGroupWorkIds(pair.FilteredEvents, pair.BatchContext, perspectiveName).ToList();
    var storedForm = await _tryRecordStoredFormFailureAsync(ex, streamId, perspectiveName, leasedRows, ct).ConfigureAwait(false);
    if (storedForm is null) {
#pragma warning disable CA1848
      // #1976: the parked rows are retried and dead-lettered at the bound, so the attempt is a Warning.
      _logger.LogWarning(ex, "Drain mode: Error processing perspective {Perspective} for stream {StreamId}", perspectiveName, streamId);
#pragma warning restore CA1848
      await _parkLeasedRowsAsync(leasedRows, ex.Message, MessageFailureReason.Unknown, ct).ConfigureAwait(false);
    }
    _metrics?.Errors.Add(1);

    var failure = new PerspectiveCursorFailure {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = Guid.Empty,
      Status = PerspectiveProcessingStatus.Failed,
      Error = storedForm ?? ex.Message
    };
    await _completionStrategy.ReportFailureAsync(failure, pair.WorkCoordinator, ct).ConfigureAwait(false);
  }

  /// <summary>
  /// The drain batch with set-based pairs (#2246). The first lap of every stream is applied together: the pairs that run
  /// per pair (the collective sink, a per-stream perspective) first, each under its own gate as a lap runs them; then the
  /// set-based pairs, whose affinity gates are taken in <see cref="PerspectiveApplyLockSet.Order"/>, begun in parallel
  /// (an inversion's rewind runs per pair there), applied by <see cref="PerspectiveApplyBatch"/> under one lease, and
  /// finished in parallel. Each stream then continues its drain from its refetch, lap by lap, per pair.
  /// </summary>
  private async Task _processDrainModeStreamsSetBasedAsync(
      AsyncServiceScope scope,
      IWorkCoordinator workCoordinator,
      Dictionary<Guid, List<MessageEnvelope<IEvent>>> eventsByStream,
      DrainBatchContext context,
      CancellationToken cancellationToken) {
    var parallel = new ParallelOptions { MaxDegreeOfParallelism = _effectiveWidth(), CancellationToken = cancellationToken };
    var setBased = new List<(Guid StreamId, string PerspectiveName, List<MessageEnvelope<IEvent>> Events)>();
    var perPair = new Dictionary<Guid, List<(string PerspectiveName, List<MessageEnvelope<IEvent>> Events)>>();
    foreach (var (streamId, events) in eventsByStream) {
      _perspectiveDrainChannel.MarkDraining(streamId);
      _registerDrainModeLifecycleTracking(events, context.TypeNameCache, streamId, context.LifecycleCoordinator);
      foreach (var perspectiveName in _collectDrainModePerspectiveNames(events, context.TypeNameCache)) {
        var filtered = _drainEventsFor(perspectiveName, events, context);
        if (filtered.Count == 0) {
          continue;
        }
        if (_writeModeOf(perspectiveName) == PerspectiveWriteMode.SetBased) {
          setBased.Add((streamId, perspectiveName, filtered));
        } else {
          if (!perPair.TryGetValue(streamId, out var list)) {
            list = [];
            perPair[streamId] = list;
          }
          list.Add((perspectiveName, filtered));
        }
      }
    }
    var continued = false;
    try {
      await Parallel.ForEachAsync(perPair, parallel, async (stream, ct) => {
        foreach (var (perspectiveName, events) in stream.Value) {
          await _drainPerspectiveGatedAsync(stream.Key, perspectiveName, events, context, ct).ConfigureAwait(false);
        }
      }).ConfigureAwait(false);

      await _drainSetBasedLapAsync(setBased, context, parallel, cancellationToken).ConfigureAwait(false);

      // Each stream's later laps, from its refetch, as its own drain would run them; it clears its in-flight marker.
      continued = true;
      await Parallel.ForEachAsync(eventsByStream, parallel, (stream, ct) => new ValueTask(_processDrainModeStreamAsync(
        scope, workCoordinator, stream.Key, stream.Value, context, ct, firstLapApplied: true))).ConfigureAwait(false);
    } finally {
      if (!continued) {
        foreach (var streamId in eventsByStream.Keys) {
          _perspectiveDrainChannel.MarkDrained(streamId);
        }
      }
    }
  }

  /// <summary>The set-based pairs of a drain batch's first lap, applied together.</summary>
  private async Task _drainSetBasedLapAsync(
      List<(Guid StreamId, string PerspectiveName, List<MessageEnvelope<IEvent>> Events)> pairs,
      DrainBatchContext context,
      ParallelOptions parallel,
      CancellationToken cancellationToken) {
    // An empty lap takes no gate and begins nothing: there is nothing to batch.
    var gates = await AcquireAffinityGatesInOrderAsync(pairs.Select(p => (p.StreamId, p.PerspectiveName)), "drain", cancellationToken).ConfigureAwait(false);
    var candidates = new ConcurrentQueue<DrainPair>();
    try {
      try {
        await Parallel.ForEachAsync(pairs, parallel, async (work, ct) => {
          var pair = await _beginDrainModePerspectiveAsync(work.StreamId, work.PerspectiveName, work.Events, context, ct).ConfigureAwait(false);
          if (pair is null) {
            return;
          }
          if (pair.InversionAnchor is null) {
            pair.StartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            pair.BatchPair = new PerspectiveApplyBatch.Pair(
              pair.StreamId, pair.PerspectiveName, pair.Runner, pair.LastProcessedEventId, pair.FilteredEvents);
            candidates.Enqueue(pair);
            return;
          }
          // A rewind replays the stream on its own, under its own lease, as the per-pair path runs it.
          await using (pair.ScopeLifetime.ConfigureAwait(false)) {
            using var lease = _drainLease(pair.StreamId, ct);
            pair.StartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _markAffinityPhase(pair.StreamId, pair.PerspectiveName, "apply");
            try {
              var result = await _rewindDrainPairAsync(pair, lease.Token).ConfigureAwait(false);
              await _finishDrainModePerspectiveAsync(pair, result, lease.Token).ConfigureAwait(false);
            } catch (OperationCanceledException leaseExpired) when (lease.Token.IsCancellationRequested && !ct.IsCancellationRequested) {
              await _failDrainPairOnLeaseExpiryAsync(pair, leaseExpired, ct).ConfigureAwait(false);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
              await _failDrainPairAsync(pair, ex, ct).ConfigureAwait(false);
            }
          }
        }).ConfigureAwait(false);

        if (candidates.IsEmpty) {
          return;
        }
        // One lease for the batch: its token cancels the prepare, the write and the completions when the batch outlives
        // the lease, and every pair of the batch is then routed to the failure path, as one pair's would be.
        using var lease = _drainLease(candidates.First().StreamId, cancellationToken);
        try {
          foreach (var pair in candidates) {
            _markAffinityPhase(pair.StreamId, pair.PerspectiveName, "apply");
          }
          await PerspectiveApplyBatch.RunAsync([.. candidates.Select(p => p.BatchPair!)], _batchServices(), lease.Token).ConfigureAwait(false);
          await Parallel.ForEachAsync(candidates, parallel, async (pair, _) => {
            try {
              if (pair.BatchPair!.Failure is { } failure) {
                await _failDrainPairAsync(pair, failure, cancellationToken).ConfigureAwait(false);
              } else {
                await _finishDrainModePerspectiveAsync(pair, pair.BatchPair.Result!, lease.Token).ConfigureAwait(false);
              }
            } catch (OperationCanceledException leaseExpired) when (lease.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
              await _failDrainPairOnLeaseExpiryAsync(pair, leaseExpired, cancellationToken).ConfigureAwait(false);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
              await _failDrainPairAsync(pair, ex, cancellationToken).ConfigureAwait(false);
            }
          }).ConfigureAwait(false);
        } catch (OperationCanceledException leaseExpired) when (lease.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
          foreach (var pair in candidates.Where(p => p.BatchPair!.Result is null && p.BatchPair.Failure is null)) {
            await _failDrainPairOnLeaseExpiryAsync(pair, leaseExpired, cancellationToken).ConfigureAwait(false);
          }
        }
      } finally {
        foreach (var pair in candidates) {
          await pair.ScopeLifetime.DisposeAsync().ConfigureAwait(false);
        }
      }
    } finally {
      gates.Dispose();
    }
  }
}
