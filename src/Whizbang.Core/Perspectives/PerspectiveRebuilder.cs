using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Events.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using static Whizbang.Core.Messaging.ProcessingModeAccessor;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Implements perspective rebuild operations in multiple modes.
/// Used internally by the migration system and available to developers for operational needs.
/// Resolves runners from IPerspectiveRunnerRegistry, queries streams from IEventStoreQuery,
/// and replays events through IPerspectiveRunner.RunAsync.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild</docs>
public sealed partial class PerspectiveRebuilder(
    IServiceScopeFactory scopeFactory,
    ILogger<PerspectiveRebuilder> logger) : IPerspectiveRebuilder {

  private readonly ConcurrentDictionary<string, RebuildStatus> _activeRebuilds = new();

  // Batch size for flushing pending cursor completions to IPerspectiveCheckpointCompleter.
  // Matches PerspectiveWorker's intent of amortizing round-trips while still persisting
  // partial progress during long rebuilds.
  private const int COMPLETION_FLUSH_BATCH_SIZE = 50;

  /// <inheritdoc/>
  public async Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) {
    return await _rebuildCoreAsync(perspectiveName, RebuildMode.BlueGreen, streamIds: null, ct, origin: null);
  }

  /// <inheritdoc/>
  public async Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, RebuildOrigin origin,
      CancellationToken ct = default) {
    return await _rebuildCoreAsync(perspectiveName, RebuildMode.BlueGreen, streamIds: null, ct, origin);
  }

  /// <inheritdoc/>
  public async Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) {
    return await _rebuildCoreAsync(perspectiveName, RebuildMode.InPlace, streamIds: null, ct, origin: null);
  }

  /// <inheritdoc/>
  public async Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, RebuildOrigin origin,
      CancellationToken ct = default) {
    return await _rebuildCoreAsync(perspectiveName, RebuildMode.InPlace, streamIds: null, ct, origin);
  }

  /// <inheritdoc/>
  public async Task<RebuildResult> RebuildStreamsAsync(
      string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) {
    var ids = streamIds.ToList();
    return await _rebuildCoreAsync(perspectiveName, RebuildMode.SelectedStreams, ids, ct, origin: null);
  }

  /// <inheritdoc/>
  public async Task<RebuildResult> RebuildStreamsAsync(
      string perspectiveName, IEnumerable<Guid> streamIds, RebuildOrigin origin,
      CancellationToken ct = default) {
    var ids = streamIds.ToList();
    return await _rebuildCoreAsync(perspectiveName, RebuildMode.SelectedStreams, ids, ct, origin);
  }

  /// <inheritdoc/>
  public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) {
    _activeRebuilds.TryGetValue(perspectiveName, out var status);
    return Task.FromResult(status);
  }

  /// <summary>
  /// Digests the targeted rows, or returns null. Never lets a digest failure fail the rebuild: the digest is
  /// evidence about the rebuild, not part of it, and "not known" is an honest value where a wrong one is not.
  /// </summary>
  private async Task<string?> _digestOrNullAsync(
      IPerspectiveRowDigest digester, string perspectiveName, IReadOnlyCollection<Guid> streamIds,
      CancellationToken ct) {
    try {
      return await digester.ComputeAsync(perspectiveName, streamIds, ct);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogRowDigestNotComputed(logger, ex, perspectiveName);
      return null;
    }
  }

  /// <summary>
  /// Publishes one rebuild lifecycle event. Best effort on purpose: a rebuild that did its work must not be
  /// reported as failed because the record of it could not be written, and the caller already has the
  /// <see cref="RebuildResult"/>. The failure is logged at Debug exactly as the rewind path does it.
  /// </summary>
  /// <remarks>
  /// Takes its own scope. The catch in <c>_rebuildCoreAsync</c> runs after that method's scope is disposed, and
  /// the failure event has to go out from there too.
  /// </remarks>
  private async Task _emitAsync<TEvent>(TEvent rebuildEvent) where TEvent : IEvent {
    try {
      await using var scope = scopeFactory.CreateAsyncScope();
      var dispatcher = scope.ServiceProvider.GetService<IDispatcher>();
      if (dispatcher is null) { return; }
      // Generic on purpose: PublishAsync routes on TEvent, so passing this as IEvent would publish under the
      // interface and the concrete type's registration would never be consulted.
      await dispatcher.AsSystem().ForAllTenants().PublishAsync(rebuildEvent);
    } catch (Exception ex) when (ex is not OperationCanceledException) {
      LogRebuildEventNotPublished(logger, ex, typeof(TEvent).Name);
    }
  }

  private async Task<RebuildResult> _rebuildCoreAsync(
      string perspectiveName, RebuildMode mode, List<Guid>? streamIds, CancellationToken ct,
      RebuildOrigin? origin = null) {

    var sw = Stopwatch.StartNew();
    RebuildRun? run = null;
    // One id per rebuild, so the Started/Completed pair for this run can be found together. Time-ordered so a
    // reader can sort rebuilds without joining anything.
    Guid rebuildStreamId = TrackedGuid.New();

    try {
      await using var scope = scopeFactory.CreateAsyncScope();
      var sp = scope.ServiceProvider;

      var registry = sp.GetRequiredService<IPerspectiveRunnerRegistry>();
      var runner = registry.GetRunner(perspectiveName, sp);

      if (runner == null) {
        var registered = string.Join(", ", registry.GetRegisteredPerspectives().Select(p => p.ClrTypeName));
        var noRunner = $"No runner found for perspective '{perspectiveName}'. Registered: {registered}";
        await _emitAsync(new PerspectiveRebuildFailed(
            rebuildStreamId, perspectiveName, mode, noRunner, 0, sw.Elapsed, origin));
        return new RebuildResult(perspectiveName, 0, 0, sw.Elapsed, false, noRunner);
      }

      // Cursor persistence: rebuild captures each runner.RunAsync return value and flushes
      // them through IPerspectiveCheckpointCompleter so wh_perspective_cursors reflects the
      // rebuild end-state. Optional dependency — when no driver registers a completer, the
      // rebuilder still updates projections and just skips cursor persistence.
      var completer = sp.GetService<IPerspectiveCheckpointCompleter>();
      run = new RebuildRun(runner, perspectiveName, mode, completer, rebuildStreamId, origin);

      // When the caller didn't supply an explicit list, narrow to streams that actually contain
      // events this perspective handles — otherwise we'd iterate every stream for every
      // perspective (most RunAsync calls become no-ops).
      var eventTypes = _effectiveEventTypes(sp, registry, perspectiveName);
      streamIds ??= await _resolveStreamIdsToReplayAsync(sp, perspectiveName, eventTypes, ct);
      streamIds = await _withoutStateBasedStreamsAsync(sp, perspectiveName, streamIds, ct);

      // Blue-green needs a driver that can build and swap a shadow table. Without one the rebuild
      // replays in place, as it always did, and says so rather than claiming a swap it never made.
      var swapper = mode == RebuildMode.BlueGreen ? sp.GetService<IPerspectiveTableSwapper>() : null;
      if (mode == RebuildMode.BlueGreen && swapper is null) {
        LogBlueGreenInPlace(logger, perspectiveName);
      }

      LogRebuildStarting(logger, mode, perspectiveName, streamIds.Count, completer != null);
      await _emitAsync(new PerspectiveRebuildStarted(
          rebuildStreamId, perspectiveName, mode, streamIds.Count, DateTimeOffset.UtcNow, origin));

      // Only for a rebuild of named streams. The targeted set is known and small there, so the digest costs in
      // proportion to the repair; a whole-perspective rebuild would have to hash the table it is about to
      // replace. Taken after the state-based guard has filtered the list, so it describes the rows actually
      // replayed rather than the rows asked for.
      var digester = mode == RebuildMode.SelectedStreams ? sp.GetService<IPerspectiveRowDigest>() : null;
      var digestBefore = digester is null
          ? null
          : await _digestOrNullAsync(digester, perspectiveName, streamIds, ct);

      // Set ambient processing mode so lifecycle receptors are suppressed during rebuild
      // unless they opt in with [FireDuringReplay]
      var previousMode = Current;
      Current = ProcessingMode.Rebuild;
      try {
        if (swapper is null) {
          await _replayStreamsAsync(run, streamIds, RebuildPhase.Replaying, ct);
        } else {
          var modelTypeName = registry.GetRegisteredPerspectives().FirstOrDefault(p => p.ClrTypeName == perspectiveName)?.ModelType;
          await _rebuildBlueGreenAsync(run, sp, swapper, new ChangeQuery(sp, eventTypes, modelTypeName), streamIds, ct);
        }
      } finally {
        Current = previousMode;
      }

      // The staged rebuild's EVICT stage (stream groups): a rebuild replays from the log, so it
      // resurrects rows whose group-cascade eviction happened before the rebuild — and the
      // cascade's edge (the journal) cannot re-fire, because the origin rows are long gone. The
      // level-triggered repair is PRESENCE: drop this follower's rows for streams absent from ALL
      // of its groups' announcers (their live tables materialize the eviction decisions —
      // decisions, not rules). Conservative all-absent rule; holds honored; direct announcers
      // only in v1 (no Bridge transitivity in presence).
      await _streamGroupPresenceReconcileAsync(perspectiveName, sp, ct);

      sw.Stop();
      LogRebuildCompleted(logger, mode, perspectiveName, run.StreamsProcessed, sw.ElapsedMilliseconds);
      var digestAfter = digester is null
          ? null
          : await _digestOrNullAsync(digester, perspectiveName, streamIds, ct);
      await _emitAsync(new PerspectiveRebuildCompleted(
          rebuildStreamId, perspectiveName, mode, run.StreamsProcessed, run.EventsReplayed, sw.Elapsed, origin,
          digestBefore, digestAfter));

      return new RebuildResult(perspectiveName, run.StreamsProcessed, run.EventsReplayed, sw.Elapsed, true, null);
    } catch (Exception ex) {
      sw.Stop();
      var streamsProcessed = run?.StreamsProcessed ?? 0;
      LogRebuildFailed(logger, ex, mode, perspectiveName, streamsProcessed, sw.ElapsedMilliseconds);
      await _emitAsync(new PerspectiveRebuildFailed(
          rebuildStreamId, perspectiveName, mode, ex.Message, streamsProcessed, sw.Elapsed, origin));
      return new RebuildResult(perspectiveName, streamsProcessed, run?.EventsReplayed ?? 0, sw.Elapsed, false, ex.Message);
    } finally {
      _activeRebuilds.TryRemove(perspectiveName, out _);
    }
  }

  /// <summary>
  /// StateBased streams (ephemeral OR compacted) are NOT a rebuildable source of truth — an ephemeral
  /// stream's events self-destruct + bodies are reaped, a compacted stream replays only to its Compacted
  /// origin — so replaying one from events would corrupt the projection. Refuse them up front (the runtime
  /// backstop to the compile-time analyzer). Optional dependency: an absent coordinator = no filtering,
  /// so engines without the flags are unaffected.
  /// </summary>
  private async Task<List<Guid>> _withoutStateBasedStreamsAsync(
      IServiceProvider sp, string perspectiveName, List<Guid> streamIds, CancellationToken ct) {
    var stateBasedGuard = sp.GetService<IWorkCoordinator>();
    if (stateBasedGuard is null || streamIds.Count == 0) {
      return streamIds;
    }
    var stateBased = await stateBasedGuard.GetStateBasedStreamIdsAsync(streamIds, ct);
    if (stateBased.Count == 0) {
      return streamIds;
    }
    var stateBasedSet = stateBased as ISet<Guid> ?? new HashSet<Guid>(stateBased);
    var kept = new List<Guid>(streamIds.Count);
    foreach (var id in streamIds) {
      if (stateBasedSet.Contains(id)) {
        LogRebuildRefusedEphemeral(logger, perspectiveName, id);
      } else {
        kept.Add(id);
      }
    }
    return kept;
  }

  /// <summary>
  /// Replays <paramref name="streamIds"/> as one phase of the rebuild, reporting the phase's progress through
  /// <see cref="GetRebuildStatusAsync"/>, and flushes the cursor completions it queued.
  /// </summary>
  private async Task _replayStreamsAsync(RebuildRun run, IReadOnlyList<Guid> streamIds, RebuildPhase phase, CancellationToken ct) {
    var status = new RebuildStatus(run.PerspectiveName, run.Mode, streamIds.Count, 0, run.StartedAt) { Phase = phase };
    _activeRebuilds[run.PerspectiveName] = status;
    var processed = 0;
    foreach (var streamId in streamIds) {
      ct.ThrowIfCancellationRequested();
      processed = await _replayStreamAsync(run, streamId, status, processed, ct);
    }
    await _flushPendingCompletionsAsync(run, processed, streamIds.Count, ct);
  }

  /// <summary>
  /// The blue-green rebuild. Every stream is replayed into a shadow table while readers keep the live table,
  /// which nothing in the rebuild touches. Writers keep writing the live table meanwhile, so the shadow is then
  /// caught up with the streams whose events were committed after the rebuild read them: up to
  /// <see cref="BlueGreenRebuildOptions.MaxCatchUpPasses"/> times while the live table stays open, and a last time
  /// inside the swap, with the live table closed to writers and still open to readers. The swap is one
  /// transaction, so a reader sees the old table or the new one, never anything between.
  /// </summary>
  /// <remarks>
  /// A caught-up stream's shadow row is deleted and the stream replayed whole, so it is folded exactly as the
  /// first pass folded it. A failed rebuild drops the shadow table and leaves the live one as it was.
  /// </remarks>
  private async Task _rebuildBlueGreenAsync(
      RebuildRun run, IServiceProvider sp, IPerspectiveTableSwapper swapper, ChangeQuery changes,
      List<Guid> streamIds, CancellationToken ct) {
    var table = await swapper.FindTableAsync(run.PerspectiveName, ct).ConfigureAwait(false)
      ?? throw new InvalidOperationException(
        $"Perspective '{run.PerspectiveName}' has no registered table, so there is nothing to rebuild blue-green.");
    var options = sp.GetService<IOptions<BlueGreenRebuildOptions>>()?.Value ?? new BlueGreenRebuildOptions();

    var mark = await changes.WatermarkAsync(ct).ConfigureAwait(false);
    var shadow = await swapper.CreateShadowAsync(table, ct).ConfigureAwait(false);
    LogBlueGreenShadowCreated(logger, run.PerspectiveName, table, shadow);
    try {
      using var redirect = PerspectiveTableRedirect.Begin(table, shadow);
      await _replayStreamsAsync(run, streamIds, RebuildPhase.Replaying, ct).ConfigureAwait(false);

      for (var pass = 0; pass < options.MaxCatchUpPasses; pass++) {
        var next = await changes.WatermarkAsync(ct).ConfigureAwait(false);
        var changed = await changes.StreamsChangedSinceAsync(mark, streamIds, ct).ConfigureAwait(false);
        mark = next;
        if (changed.Count == 0) {
          break;
        }
        await _catchUpAsync(run, swapper, shadow, changed, RebuildPhase.CatchingUp, ct).ConfigureAwait(false);
      }

      var swap = new PerspectiveTableSwap(table, shadow, options.KeepPreviousTable, options.SwapLockTimeout);
      var previous = await swapper.SwapAsync(swap, async lockedCt => {
        var changed = await changes.StreamsChangedSinceAsync(mark, streamIds, lockedCt).ConfigureAwait(false);
        await _catchUpAsync(run, swapper, shadow, changed, RebuildPhase.Swapping, lockedCt).ConfigureAwait(false);
      }, ct).ConfigureAwait(false);
      LogBlueGreenSwapped(logger, run.PerspectiveName, table, previous ?? "(dropped)");
    } catch {
      await _dropShadowAsync(swapper, run.PerspectiveName, shadow).ConfigureAwait(false);
      throw;
    }
  }

  /// <summary>Deletes the shadow rows of <paramref name="changed"/> and replays those streams whole.</summary>
  private async Task _catchUpAsync(
      RebuildRun run, IPerspectiveTableSwapper swapper, string shadow, IReadOnlyList<Guid> changed, RebuildPhase phase,
      CancellationToken ct) {
    if (changed.Count > 0) {
      await swapper.DeleteRowsAsync(shadow, changed, ct).ConfigureAwait(false);
    }
    LogBlueGreenCatchingUp(logger, run.PerspectiveName, phase, changed.Count);
    await _replayStreamsAsync(run, changed, phase, ct).ConfigureAwait(false);
  }

  /// <summary>
  /// Drops a failed rebuild's shadow table. Best effort and never canceled: the failure being reported is the
  /// rebuild's, and a shadow left behind is replaced by the next rebuild's.
  /// </summary>
  private async Task _dropShadowAsync(IPerspectiveTableSwapper swapper, string perspectiveName, string shadow) {
    try {
      await swapper.DropAsync(shadow, CancellationToken.None).ConfigureAwait(false);
    } catch (Exception ex) {
      LogBlueGreenShadowDropFailed(logger, ex, perspectiveName, shadow);
    }
  }

  /// <summary>
  /// The event types this perspective folds, widened by the source types of upcasters that change an event's
  /// type into one of them; null when the registry carries no event-type metadata for the perspective (rare —
  /// incomplete registration), which means every stream.
  /// </summary>
  private static IReadOnlyList<string>? _effectiveEventTypes(
      IServiceProvider sp, IPerspectiveRunnerRegistry registry, string perspectiveName) {
    var relevantEventTypes = registry.GetRegisteredPerspectives()
        .FirstOrDefault(p => p.ClrTypeName == perspectiveName)?.EventTypes;
    if (relevantEventTypes is not { Count: > 0 }) {
      return null;
    }
    // A type-change upcaster (LegacyA → GenericB) lets a stream that only carries the legacy
    // input feed this perspective on rebuild — but such a stream has none of the perspective's
    // own subscribed types, so the scoping below would skip it. Widen the scope with those
    // upcasters' source-type names (scoped to upcasters whose target this perspective subscribes
    // to). Empty for the common no-type-change case, so the query shape is unchanged there.
    var extraNames = sp.GetService<EventUpcasterPipeline>()?.ExtraInputTypeNamesFor(relevantEventTypes);
    if (extraNames is not { Count: > 0 }) {
      return relevantEventTypes;
    }
    var widened = new List<string>(relevantEventTypes);
    widened.AddRange(extraNames);
    return widened;
  }

  /// <summary>
  /// Narrows rebuild scope to streams that actually contain events this perspective handles,
  /// falling back to every stream when the perspective's event-type metadata is missing (rare —
  /// incomplete registration). The source-generated registry supplies the event-type set.
  /// </summary>
  private async Task<List<Guid>> _resolveStreamIdsToReplayAsync(
      IServiceProvider sp, string perspectiveName, IReadOnlyList<string>? eventTypes, CancellationToken ct) {
    var eventStoreQuery = sp.GetRequiredService<IEventStoreQuery>();
    if (eventTypes is not null) {
      var streamIds = await eventStoreQuery.Query
          .Where(e => eventTypes.Contains(e.EventType))
          .Select(e => e.StreamId)
          .Distinct()
          .ToListAsync(ct);
      LogStreamScoping(logger, perspectiveName, streamIds.Count, eventTypes.Count);
      return streamIds;
    }

    var fallback = await eventStoreQuery.Query
        .Select(e => e.StreamId)
        .Distinct()
        .ToListAsync(ct);
    LogStreamScopingFallback(logger, perspectiveName, fallback.Count);
    return fallback;
  }

  /// <summary>
  /// Replays a single stream: runner.RunAsync, log, queue the cursor completion for flush, and
  /// periodic progress reporting. Exceptions are logged (LogStreamFailed) and swallowed so one
  /// bad stream doesn't kill the rebuild. Returns the updated (streamsProcessed, eventsReplayed)
  /// counters so the caller's tuple stays coherent.
  /// </summary>
  private static async Task _streamGroupPresenceReconcileAsync(
      string perspectiveName, IServiceProvider sp, CancellationToken ct) {
    var model = PerspectiveStreamGroupRegistry.RegisteredModels()
      .FirstOrDefault(m => TypeNameFormatter.TryFormatClrTypeName(m, out var clr) && string.Equals(clr, perspectiveName, StringComparison.Ordinal));
    if (model is null) {
      return; // not a group member — nothing to reconcile.
    }
    var followMemberships = PerspectiveStreamGroupRegistry.Resolve(model).Where(m => m.Follow).ToList();
    if (followMemberships.Count == 0) {
      return;
    }
    var coordinator = sp.GetService<Whizbang.Core.Messaging.IWorkCoordinator>();
    if (coordinator is null) {
      return;
    }

    // Every announcer whose evictions can REACH this follower — direct group siblings plus
    // whatever a Bridge carries across. Reachability shares Compute's dial semantics, so the
    // witness set and the live cascade can never disagree about who evicts whom.
    var membershipMap = PerspectiveStreamGroupRegistry.RegisteredModels()
      .ToDictionary(m => m, PerspectiveStreamGroupRegistry.Resolve);
    var announcers = StreamGroupClosure.ReachableAnnouncers(model, membershipMap);
    if (announcers.Count == 0) {
      return;
    }

    var names = new List<string> { perspectiveName };
    names.AddRange(announcers.Select(a => TypeNameFormatter.TryFormatClrTypeName(a, out var clr) ? clr : null).Where(n => n is not null).Cast<string>());
    var tables = await coordinator.GetPerspectiveTableNamesAsync(names, ct).ConfigureAwait(false);
    var followerTable = tables.FirstOrDefault(t => t.ClrTypeName == perspectiveName)?.TableName;
    var announcerTables = tables.Where(t => t.ClrTypeName != perspectiveName).Select(t => t.TableName).ToList();
    if (followerTable is null || announcerTables.Count == 0) {
      return;
    }
    await coordinator.ReconcileFollowerPresenceAsync(followerTable, announcerTables, ct).ConfigureAwait(false);
  }

  /// <summary>
  /// Replays a single stream: runner.RunRebuildAsync, log, queue the cursor completions for flush, and
  /// periodic progress reporting. Exceptions are logged (LogStreamFailed) and swallowed so one bad stream
  /// doesn't kill the rebuild. Returns the phase's processed count.
  /// </summary>
  private async Task<int> _replayStreamAsync(
      RebuildRun run, Guid streamId, RebuildStatus status, int processed, CancellationToken ct) {
    var streamSw = Stopwatch.StartNew();
    try {
      // RunRebuildAsync replays the physical stream and returns one completion per TARGET stream.
      // Without re-key upcasters this is a single completion equal to streamId (identical to the
      // old RunAsync path); with a re-key upcaster, events fan out onto their target rows and a
      // cursor is queued for each. Processed counts physical streams (one per call) so the
      // rebuild result stays keyed to the resolved stream list.
      var completions = await run.Runner.RunRebuildAsync(streamId, run.PerspectiveName, ct);
      streamSw.Stop();
      processed++;
      run.StreamsProcessed++;
      run.EventsReplayed += completions.Count;

      foreach (var completion in completions) {
        LogStreamReplayed(logger, run.PerspectiveName, completion.StreamId, completion.LastEventId,
            completion.Status, streamSw.ElapsedMilliseconds, processed, status.TotalStreams);

        if (run.PendingCompletions != null) {
          run.PendingCompletions.Add(completion);
          if (run.PendingCompletions.Count >= COMPLETION_FLUSH_BATCH_SIZE) {
            await _flushPendingCompletionsAsync(run, processed, status.TotalStreams, ct);
          }
        }
      }

      if (processed % 100 == 0 || processed == status.TotalStreams) {
        _activeRebuilds[run.PerspectiveName] = status with { ProcessedStreams = processed };
        LogRebuildProgress(logger, run.PerspectiveName, processed, status.TotalStreams,
            run.Elapsed.ElapsedMilliseconds);
        // Documented as emitted "periodically during rebuild", so it is emitted on the same bounded cadence the
        // log uses rather than per stream. A long rebuild reports progress; a short one reports once at the end.
        await _emitAsync(new PerspectiveRebuildProgress(
            run.RebuildStreamId, run.PerspectiveName, run.Mode, processed, status.TotalStreams,
            run.EventsReplayed, run.StartedAt, run.Origin));
      }
    } catch (Exception ex) {
      streamSw.Stop();
      LogStreamFailed(logger, ex, run.PerspectiveName, streamId, processed, status.TotalStreams);
    }
    return processed;
  }

  /// <summary>Flushes any queued cursor completions through the optional
  /// <see cref="IPerspectiveCheckpointCompleter"/> and logs the batch size + flush time.
  /// No-op when the completer or pending list is null/empty.</summary>
  private async Task _flushPendingCompletionsAsync(RebuildRun run, int processed, int totalStreams, CancellationToken ct) {
    if (run.Completer is null || run.PendingCompletions is not { Count: > 0 } pending) {
      return;
    }
    var flushSw = Stopwatch.StartNew();
    var flushCount = pending.Count;
    await run.Completer.CompleteAsync(pending, ct);
    flushSw.Stop();
    LogCursorFlushed(logger, run.PerspectiveName, flushCount, flushSw.ElapsedMilliseconds, processed, totalStreams);
    pending.Clear();
  }

  /// <summary>One rebuild's fixed inputs and running totals, threaded through its phases.</summary>
  private sealed class RebuildRun(
      IPerspectiveRunner runner, string perspectiveName, RebuildMode mode, IPerspectiveCheckpointCompleter? completer,
      Guid rebuildStreamId, RebuildOrigin? origin) {
    public IPerspectiveRunner Runner { get; } = runner;
    public string PerspectiveName { get; } = perspectiveName;
    public RebuildMode Mode { get; } = mode;
    /// <summary>The id shared by this run's Started/Progress/Completed events.</summary>
    public Guid RebuildStreamId { get; } = rebuildStreamId;
    /// <summary>Who asked, carried onto the progress events as well so a reader need not join back to Started.</summary>
    public RebuildOrigin? Origin { get; } = origin;
    public IPerspectiveCheckpointCompleter? Completer { get; } = completer;
    public List<PerspectiveCursorCompletion>? PendingCompletions { get; } = completer is null ? null : new(64);
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public Stopwatch Elapsed { get; } = Stopwatch.StartNew();
    public int StreamsProcessed { get; set; }
    public int EventsReplayed { get; set; }
  }

  /// <summary>
  /// What a blue-green rebuild asks the event store while it runs: the commit-sequence watermark, and the streams
  /// whose events were committed after one.
  /// </summary>
  /// <remarks>
  /// The commit sequence is stamped after commit and only ever grows, so an event committed after a watermark was
  /// read is stamped above it, or not yet stamped at all; both count as changed. A stream already replayed with an
  /// event that was merely stamped late is replayed once more, which folds it the same way.
  /// </remarks>
  private sealed class ChangeQuery(IServiceProvider sp, IReadOnlyList<string>? eventTypes, string? modelTypeName) {
    private readonly IEventStoreQuery _events = sp.GetRequiredService<IEventStoreQuery>();
    private readonly IReadOnlyList<string> _collectiveTypes = modelTypeName is null
      ? []
      : sp.GetService<ICollectiveReplayApplier>()?.CollectiveEventTypeNamesFor(modelTypeName) ?? [];

    /// <summary>The highest commit sequence stamped so far, or 0 when none is.</summary>
    public async Task<long> WatermarkAsync(CancellationToken ct) {
      var top = await _events.Query
        .Where(e => e.CommitSequence != null)
        .OrderByDescending(e => e.CommitSequence)
        .Select(e => e.CommitSequence)
        .Take(1)
        .ToListAsync(ct);
      return top.Count == 0 ? 0 : top[0]!.Value;
    }

    /// <summary>
    /// The streams with events this perspective folds committed after <paramref name="mark"/>. A collective
    /// committed after it can change any row, so it makes every stream changed, along with any new ones.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> StreamsChangedSinceAsync(long mark, IReadOnlyList<Guid> allStreams, CancellationToken ct) {
      var recent = _events.Query.Where(e => e.CommitSequence == null || e.CommitSequence > mark);
      var perspectiveEvents = eventTypes is null ? recent : recent.Where(e => eventTypes.Contains(e.EventType));
      var changed = await perspectiveEvents.Select(e => e.StreamId).Distinct().ToListAsync(ct);
      if (_collectiveTypes.Count == 0) {
        return changed;
      }
      var collectives = await recent.Where(e => _collectiveTypes.Contains(e.EventType)).Select(e => e.Id).Take(1).ToListAsync(ct);
      return collectives.Count == 0 ? changed : [.. allStreams.Union(changed)];
    }
  }

  [LoggerMessage(Level = LogLevel.Warning,
      Message = "Blue-green rebuild of {Perspective}: the driver has no shadow-table support, so it is rebuilt in place")]
  private static partial void LogBlueGreenInPlace(ILogger logger, string perspective);

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Blue-green rebuild of {Perspective}: replaying into {Shadow} while {Table} keeps serving reads")]
  private static partial void LogBlueGreenShadowCreated(ILogger logger, string perspective, string table, string shadow);

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Blue-green rebuild of {Perspective}: {Phase}, {Count} stream(s) written during the rebuild")]
  private static partial void LogBlueGreenCatchingUp(ILogger logger, string perspective, RebuildPhase phase, int count);

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Blue-green rebuild of {Perspective}: the rebuilt table is now {Table}; the previous one is {Previous}")]
  private static partial void LogBlueGreenSwapped(ILogger logger, string perspective, string table, string previous);

  [LoggerMessage(Level = LogLevel.Warning,
      Message = "Blue-green rebuild of {Perspective} failed and its shadow table {Shadow} could not be dropped; the next rebuild replaces it")]
  private static partial void LogBlueGreenShadowDropFailed(ILogger logger, Exception ex, string perspective, string shadow);

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Starting {Mode} rebuild of perspective {Perspective} — {StreamCount} streams; cursor persistence enabled={CursorPersistence}")]
  private static partial void LogRebuildStarting(ILogger logger, RebuildMode mode, string perspective, int streamCount, bool cursorPersistence);

  [LoggerMessage(Level = LogLevel.Debug,
      Message = "Could not publish rebuild event {EventType}; the rebuild itself is unaffected")]
  private static partial void LogRebuildEventNotPublished(ILogger logger, Exception ex, string eventType);

  [LoggerMessage(Level = LogLevel.Debug,
      Message = "Could not digest the rebuilt rows for {Perspective}; the rebuild itself is unaffected")]
  private static partial void LogRowDigestNotComputed(ILogger logger, Exception ex, string perspective);

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Completed {Mode} rebuild of perspective {Perspective} — {Streams} streams in {ElapsedMs}ms")]
  private static partial void LogRebuildCompleted(ILogger logger, RebuildMode mode, string perspective, int streams, long elapsedMs);

  [LoggerMessage(Level = LogLevel.Error,
      Message = "Failed {Mode} rebuild of perspective {Perspective} after {Streams} streams in {ElapsedMs}ms")]
  private static partial void LogRebuildFailed(ILogger logger, Exception ex, RebuildMode mode, string perspective, int streams, long elapsedMs);

  [LoggerMessage(Level = LogLevel.Warning,
      Message = "Rebuild {Perspective}: failed on stream {StreamId} ({Processed}/{Total})")]
  private static partial void LogStreamFailed(ILogger logger, Exception ex, string perspective, Guid streamId, int processed, int total);

  [LoggerMessage(Level = LogLevel.Warning,
      Message = "Rebuild {Perspective}: refused ephemeral stream {StreamId} — ephemeral streams are not a rebuildable source of truth (events self-destruct, bodies are reaped). Skipped.")]
  private static partial void LogRebuildRefusedEphemeral(ILogger logger, string perspective, Guid streamId);

  [LoggerMessage(Level = LogLevel.Debug,
      Message = "Rebuild {Perspective}: stream {StreamId} replayed to event {LastEventId} (status {Status}) in {ElapsedMs}ms ({Processed}/{Total})")]
  [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "LoggerMessage source-generated method — parameter list mirrors the structured log template placeholders and cannot be grouped without losing structured-logging semantics.")]
  private static partial void LogStreamReplayed(ILogger logger, string perspective, Guid streamId,
      Guid lastEventId, PerspectiveProcessingStatus status, long elapsedMs, int processed, int total);

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Rebuild {Perspective}: progress {Processed}/{Total} streams, elapsed {ElapsedMs}ms")]
  private static partial void LogRebuildProgress(ILogger logger, string perspective, int processed,
      int total, long elapsedMs);

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Rebuild {Perspective}: persisted {Count} cursor checkpoint(s) in {ElapsedMs}ms at {Processed}/{Total} streams")]
  private static partial void LogCursorFlushed(ILogger logger, string perspective, int count,
      long elapsedMs, int processed, int total);

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Rebuild {Perspective}: scoped to {StreamCount} stream(s) across {EventTypeCount} event type(s)")]
  private static partial void LogStreamScoping(ILogger logger, string perspective, int streamCount,
      int eventTypeCount);

  [LoggerMessage(Level = LogLevel.Warning,
      Message = "Rebuild {Perspective}: no event-type metadata in runner registry; falling back to iterating all {StreamCount} streams")]
  private static partial void LogStreamScopingFallback(ILogger logger, string perspective,
      int streamCount);
}

/// <summary>
/// Extension for IQueryable to support ToListAsync in Core (no EF Core dependency).
/// </summary>
internal static class QueryableExtensions {
  internal static async Task<List<T>> ToListAsync<T>(this IQueryable<T> source, CancellationToken ct) {
    var list = new List<T>();
    if (source is IAsyncEnumerable<T> asyncEnumerable) {
      await foreach (var item in asyncEnumerable.WithCancellation(ct)) {
        list.Add(item);
      }
    } else {
      list.AddRange(source);
    }
    return list;
  }
}
