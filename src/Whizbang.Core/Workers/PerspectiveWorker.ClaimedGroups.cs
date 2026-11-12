// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Workers;

/// <summary>
/// The claimed path's per-group work, split into the phases a set-based batch runs them in (#2246): begin (resolve the
/// group, fire the pre-perspective stages, run a rewind or bootstrap, read the events), apply (the per-pair runner or the
/// batch), and finish (report the completion, signal sync, fire the post-perspective stages). The per-pair path runs the
/// three in a row for one group; the set-based path runs each phase for every group of the batch before the next.
/// </summary>
public partial class PerspectiveWorker {
  /// <summary>What every group of one claimed batch shares.</summary>
  private sealed record ClaimedBatchContext(
      Activity? BatchActivity,
      ActivityContext EffectiveParent,
      ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)> ProcessedEvents,
      ConcurrentDictionary<Guid, bool> IsNewByEventId);

  /// <summary>
  /// What a claimed set-based batch read for one of its pairs with one read for every pair (#2246): the pair's cursor and
  /// its stream's events after it, of every event type, in the stream's order.
  /// </summary>
  private sealed record ClaimedPrefetch(PerspectiveCursorInfo? Checkpoint, IReadOnlyList<MessageEnvelope<IEvent>> StreamEvents) {
    /// <summary>The events in the order the per-pair path loads them (by id), for the lifecycle stages and the trace.</summary>
    public List<MessageEnvelope<IEvent>> Upcoming { get; } = [.. StreamEvents.OrderByMessageId()];
  }

  /// <summary>One claimed group between its phases: its scope, what it resolved, and its span.</summary>
  private sealed class ClaimedGroup(
      IGrouping<(Guid StreamId, string PerspectiveName), PerspectiveWork> work, AsyncServiceScope scope) : IAsyncDisposable {
    public IGrouping<(Guid StreamId, string PerspectiveName), PerspectiveWork> Work { get; } = work;
    public AsyncServiceScope Scope { get; } = scope;
    public Guid StreamId => Work.Key.StreamId;
    public string PerspectiveName => Work.Key.PerspectiveName;
    public IWorkCoordinator WorkCoordinator { get; init; } = null!;
    public IReceptorInvoker? ReceptorInvoker { get; init; }
    public ILifecycleCoordinator? LifecycleCoordinator { get; init; }
    public PerspectiveCursorInfo? Checkpoint { get; set; }
    public IPerspectiveRunner? Runner { get; set; }
    public IEventStore? EventStore { get; set; }
    public List<MessageEnvelope<IEvent>>? UpcomingEvents { get; set; }
    public Activity? PerspectiveActivity { get; set; }
    public bool EnablePerspectiveSpans { get; set; }
    public bool EnableLifecycleSpans { get; set; }
    public PerspectiveStreamContext StreamContext { get; set; }

    /// <summary>What the batch read for this group, when it read its pairs together.</summary>
    public ClaimedPrefetch? Prefetched { get; init; }

    /// <summary>The pair this group applies in a set-based batch.</summary>
    public PerspectiveApplyBatch.Pair? BatchPair { get; set; }

    public async ValueTask DisposeAsync() {
      PerspectiveActivity?.Dispose();
      await Scope.DisposeAsync().ConfigureAwait(false);
    }
  }

  /// <summary>
  /// The write mode of a perspective's pairs: its declared mode, else the service's; per stream for a perspective with
  /// no generated runner.
  /// </summary>
  private PerspectiveWriteMode _writeModeOf(string perspectiveName) =>
    perspectiveName == CollectiveRouting.SINK_PERSPECTIVE_NAME
      ? PerspectiveWriteMode.PerStream
      : PerspectiveWriteModes.Resolve(perspectiveName, _options.WriteMode);

  /// <summary>One claimed group on the per-pair path, under the affinity gate its caller holds.</summary>
  private async Task _runClaimedGroupAsync(
      ClaimedBatchContext batch,
      IGrouping<(Guid StreamId, string PerspectiveName), PerspectiveWork> work,
      CancellationToken ct) {
    var group = await _beginClaimedGroupAsync(batch, work, ct).ConfigureAwait(false);
    if (group is null) {
      return;
    }
    await using (group.ConfigureAwait(false)) {
      try {
        _markAffinityPhase(group.StreamId, group.PerspectiveName, "apply");
        var (result, processingMode, rewindLockSkipped) = await _executePerspectiveRunnerAsync(
          group.Work, group.Runner!, group.Checkpoint, group.StreamContext, group.EnablePerspectiveSpans, ct).ConfigureAwait(false);
        if (rewindLockSkipped) {
          return;
        }
        await _finishClaimedGroupAsync(batch, group, result, processingMode, ct).ConfigureAwait(false);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        await _failClaimedGroupAsync(group, ex, ct).ConfigureAwait(false);
      }
    }
  }

  /// <summary>
  /// Begins a claimed group: its scope, the collective sink (run here in full), the runner, the cursor and events, the
  /// perspective span and the pre-perspective stages. Null when the group has ended (the collective sink, no runner, or
  /// a pre-perspective stage that failed, whose failure is already handled).
  /// </summary>
  private async Task<ClaimedGroup?> _beginClaimedGroupAsync(
      ClaimedBatchContext batch,
      IGrouping<(Guid StreamId, string PerspectiveName), PerspectiveWork> work,
      CancellationToken ct,
      ClaimedPrefetch? prefetched = null) {
    var streamId = work.Key.StreamId;
    var perspectiveName = work.Key.PerspectiveName;
    var scope = _scopeFactory.CreateAsyncScope();
    var groupWorkCoordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    var group = new ClaimedGroup(work, scope) {
      WorkCoordinator = groupWorkCoordinator,
      ReceptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>(),
      LifecycleCoordinator = scope.ServiceProvider.GetService<ILifecycleCoordinator>(),
      Prefetched = prefetched,
    };
    var begun = false;
    try {
      // Collective-event sink: a collective event is routed (migration 061) to the fixed
      // __collective__ perspective, not a per-stream runner. Dispatch it via ICollectiveDispatcher
      // and skip the normal runner path entirely.
      if (perspectiveName == CollectiveRouting.SINK_PERSPECTIVE_NAME) {
        // The leased group IS the set of __collective__ work rows for this stream; their WorkIds
        // (event_work_id) are what the sink must complete on success so the rows are deleted.
        var sinkWorkIds = work.Select(w => w.WorkId).Where(id => id != Guid.Empty).Distinct().ToArray();
        _markAffinityPhase(streamId, perspectiveName, "collective");
        await _processCollectiveSinkAsync(scope, groupWorkCoordinator, streamId, sinkWorkIds, ct).ConfigureAwait(false);
        return null;
      }

      _markAffinityPhase(streamId, perspectiveName, "resolve");
      var (checkpoint, runner, eventStore, upcomingEvents, perspectiveParentContext) =
        await _resolveDependenciesAndLoadEventsAsync(
          scope, groupWorkCoordinator, group.ReceptorInvoker, streamId, perspectiveName,
          batch.BatchActivity, batch.EffectiveParent, ct, prefetched).ConfigureAwait(false);
      if (runner is null) {
        return null;
      }
      group.Checkpoint = checkpoint;
      group.Runner = runner;
      group.EventStore = eventStore;
      group.UpcomingEvents = upcomingEvents;

      group.EnablePerspectiveSpans = _tracingOptions.CurrentValue.IsEnabled(TraceComponents.Perspectives);
      group.PerspectiveActivity = group.EnablePerspectiveSpans
        ? WhizbangActivitySource.Tracing.StartActivity(
            $"Perspective {perspectiveName}",
            ActivityKind.Internal,
            parentContext: perspectiveParentContext)
        : null;
      _tagPerspectiveActivity(group.PerspectiveActivity, perspectiveName, streamId, upcomingEvents, perspectiveParentContext);

      group.EnableLifecycleSpans = _tracingOptions.CurrentValue.IsEnabled(TraceComponents.Lifecycle);
      group.StreamContext = new PerspectiveStreamContext(streamId, perspectiveName, checkpoint?.LastEventId, scope.ServiceProvider);

      try {
        _markAffinityPhase(streamId, perspectiveName, "pre-lifecycle");
        await _invokePrePerspectiveLifecycleAsync(
          upcomingEvents, group.EnableLifecycleSpans, group.LifecycleCoordinator, group.ReceptorInvoker,
          group.StreamContext, runner, ct).ConfigureAwait(false);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        await _failClaimedGroupAsync(group, ex, ct).ConfigureAwait(false);
        return null;
      }
      begun = true;
      return group;
    } finally {
      if (!begun) {
        await group.DisposeAsync().ConfigureAwait(false);
      }
    }
  }

  /// <summary>
  /// What follows a claimed group's apply: the processed events, their lifecycle bookkeeping, the completion report and
  /// sync signal, the post-perspective stages, and the group's metrics.
  /// </summary>
  private async Task _finishClaimedGroupAsync(
      ClaimedBatchContext batch,
      ClaimedGroup group,
      PerspectiveCursorCompletion result,
      ProcessingMode? processingMode,
      CancellationToken ct) {
    var streamId = group.StreamId;
    var perspectiveName = group.PerspectiveName;
    var lastProcessedEventId = group.Checkpoint?.LastEventId;
    if (result.Status == PerspectiveProcessingStatus.Completed) {
      _storedFormFailures.Recovered(perspectiveName, streamId);
    }

    _markAffinityPhase(streamId, perspectiveName, "load-processed");
    var processedEvents = await _loadAndLogProcessedEventsAsync(
      group.ReceptorInvoker, group.EventStore, result, streamId, perspectiveName,
      lastProcessedEventId, ct, group.Prefetched).ConfigureAwait(false);

    // Rewind path: the range-based load above covers events above the pre-rewind
    // cursor. Events below the cursor (the rewind trigger AND any other late arrivals
    // that accumulated during the rewind window) live in the perspective work queue;
    // IPerspectiveReplayReader is the authoritative source for that "is_new" set.
    // When registered, we use it to pull every pending event the rewind should fire
    // handlers for. When not registered, we fall back to the narrow trigger-only
    // lookup so existing deployments keep working.
    if (processingMode == ProcessingMode.Replay) {
      // Replay is returned only for a checkpoint that carries a rewind trigger
      // (_executePerspectiveRunnerAsync).
      var triggerId = group.Checkpoint!.RewindTriggerEventId!.Value;
      var replayReader = group.Scope.ServiceProvider.GetService<IPerspectiveReplayReader>();
      if (replayReader is not null && _eventTypeProvider.IsAvailable) {
        var eventTypes = _eventTypeProvider.GetEventTypes();
        var seen = processedEvents.Select(e => e.MessageId.Value).ToHashSet();
        await foreach (var annotated in replayReader.ReadReplayEventsAsync(
            streamId, perspectiveName, fromVersionExclusive: 0, eventTypes, ct).ConfigureAwait(false)) {
          if (annotated.IsNew && seen.Add(annotated.Envelope.MessageId.Value)) {
            processedEvents.Insert(0, annotated.Envelope);
          }
        }
      } else if (group.EventStore is not null
                 && _eventTypeProvider.IsAvailable
                 && !processedEvents.Any(e => e.MessageId.Value == triggerId)) {
        var envelopesUpToTrigger = await group.EventStore.GetEventsBetweenPolymorphicAsync(
          streamId,
          afterEventId: null,
          upToEventId: triggerId,
          _eventTypeProvider.GetEventTypes(),
          ct).ConfigureAwait(false);
        var triggerEnvelope = envelopesUpToTrigger
          .FirstOrDefault(e => e.MessageId.Value == triggerId);
        if (triggerEnvelope is not null) {
          processedEvents.Insert(0, triggerEnvelope);
        }
      }
    }

    foreach (var envelope in processedEvents) {
      var id = envelope.MessageId.Value;
      batch.ProcessedEvents.TryAdd(id, (envelope, streamId));
      batch.IsNewByEventId.AddOrUpdate(id, true, (_, _) => true);
    }

    _markAffinityPhase(streamId, perspectiveName, "report");
    await _reportCompletionAndSignalSyncAsync(
      result, processedEvents, group.WorkCoordinator, streamId, perspectiveName, ct).ConfigureAwait(false);
    _markAffinityPhase(streamId, perspectiveName, "post-lifecycle");
    await _invokePostPerspectiveLifecycleAsync(
      processedEvents, group.ReceptorInvoker, group.StreamContext, result,
      new PostPerspectiveLifecycleOptions(group.EnableLifecycleSpans, processingMode), ct).ConfigureAwait(false);
    _bufferCompletionsAndUpdateCache(group.Work, processedEvents, group.LifecycleCoordinator, perspectiveName);

    if (processedEvents.Count > 0) {
      OnPerspectiveEventProcessed?.Invoke(new PerspectiveEventProcessedEvent {
        PerspectiveName = perspectiveName,
        StreamId = streamId,
        EventCount = processedEvents.Count,
        EventIds = processedEvents.ConvertAll(e => e.MessageId.Value)
      });
    }
    _metrics?.StreamsUpdated.Add(1);
    if (processedEvents.Count > 0) {
      _metrics?.EventsProcessed.Add(processedEvents.Count);
    }
  }

  /// <summary>
  /// A claimed group's whole failure treatment: the failure is recorded, the group's rows are parked and the cursor
  /// failure is reported. Contained, never rethrown: letting it out made the fan-out cancel its token and stop taking
  /// groups, so every sibling group in the batch that had not yet started was dropped without running (issue #993).
  /// </summary>
  private async Task _failClaimedGroupAsync(ClaimedGroup group, Exception ex, CancellationToken ct) {
    var streamId = group.StreamId;
    var perspectiveName = group.PerspectiveName;
    var leasedRows = group.Work.Select(w => w.WorkId).Where(id => id != Guid.Empty).Distinct().ToList();
    var storedForm = await _tryRecordStoredFormFailureAsync(ex, streamId, perspectiveName, leasedRows, ct).ConfigureAwait(false);
    if (storedForm is null) {
      LogErrorProcessingPerspectiveCursor(_logger, ex, perspectiveName, streamId);
      await _parkLeasedRowsAsync(leasedRows, ex.Message, MessageFailureReason.Unknown, ct).ConfigureAwait(false);
    }
    _metrics?.Errors.Add(1);
    if (group.UpcomingEvents is { Count: > 0 } upcomingEvents) {
      var failedEventIds = upcomingEvents.ConvertAll(e => e.MessageId.Value);
      _syncEventTracker.MarkProcessedByPerspective((IEnumerable<Guid>)failedEventIds, perspectiveName);
    }
    var failure = new PerspectiveCursorFailure {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = Guid.Empty,
      Status = PerspectiveProcessingStatus.Failed,
      Error = storedForm ?? ex.Message
    };
    await _completionStrategy.ReportFailureAsync(failure, group.WorkCoordinator, ct).ConfigureAwait(false);
  }

  /// <summary>
  /// The claimed batch with set-based pairs (#2246). Every group's affinity gate is taken first, in
  /// <see cref="PerspectiveApplyLockSet.Order"/>, so two batches cannot deadlock. Each group then begins in parallel; a
  /// per-stream group, a rewind, a bootstrap's group and a runner that does not expose its read run the per-pair path in
  /// full. The rest are applied together by <see cref="PerspectiveApplyBatch"/>, then each is finished as the per-pair
  /// path finishes it.
  /// </summary>
  private async Task _processClaimedGroupsSetBasedAsync(
      List<IGrouping<(Guid StreamId, string PerspectiveName), PerspectiveWork>> groupedWork,
      ClaimedBatchContext batch,
      CancellationToken cancellationToken) {
    var parallel = new ParallelOptions { MaxDegreeOfParallelism = _effectiveWidth(), CancellationToken = cancellationToken };
    var gates = await AcquireAffinityGatesInOrderAsync(groupedWork.Select(g => g.Key), "standard", cancellationToken).ConfigureAwait(false);
    var candidates = new ConcurrentQueue<ClaimedGroup>();
    try {
      try {
        // Under the gates, as each pair's own reads would be: one cursor read and one event read for every set-based pair.
        var prefetched = await _prefetchClaimedAsync(
          [.. groupedWork.Select(g => g.Key).Where(k => _writeModeOf(k.PerspectiveName) == PerspectiveWriteMode.SetBased)],
          cancellationToken).ConfigureAwait(false);
        await Parallel.ForEachAsync(groupedWork, parallel, async (work, ct) => {
          if (_writeModeOf(work.Key.PerspectiveName) != PerspectiveWriteMode.SetBased) {
            await _runClaimedGroupAsync(batch, work, ct).ConfigureAwait(false);
            return;
          }
          var group = await _beginClaimedGroupAsync(batch, work, ct, prefetched?.GetValueOrDefault(work.Key)).ConfigureAwait(false);
          if (group is null) {
            return;
          }
          var queued = false;
          try {
            queued = await _beginClaimedApplyAsync(batch, group, ct).ConfigureAwait(false);
            if (queued) {
              candidates.Enqueue(group);
            }
          } catch (Exception ex) when (ex is not OperationCanceledException) {
            await _failClaimedGroupAsync(group, ex, ct).ConfigureAwait(false);
          } finally {
            if (!queued) {
              await group.DisposeAsync().ConfigureAwait(false);
            }
          }
        }).ConfigureAwait(false);

        var pairs = candidates.Select(g => g.BatchPair!).ToList();
        foreach (var group in candidates) {
          _markAffinityPhase(group.StreamId, group.PerspectiveName, "apply");
        }
        await PerspectiveApplyBatch.RunAsync(pairs, _batchServices(), cancellationToken).ConfigureAwait(false);

        await Parallel.ForEachAsync(candidates, parallel, async (group, ct) => {
          try {
            if (group.BatchPair!.Failure is { } failure) {
              await _failClaimedGroupAsync(group, failure, ct).ConfigureAwait(false);
            } else {
              await _finishClaimedGroupAsync(batch, group, group.BatchPair.Result!, processingMode: null, ct).ConfigureAwait(false);
            }
          } catch (Exception ex) when (ex is not OperationCanceledException) {
            await _failClaimedGroupAsync(group, ex, ct).ConfigureAwait(false);
          }
        }).ConfigureAwait(false);
      } finally {
        foreach (var group in candidates) {
          await group.DisposeAsync().ConfigureAwait(false);
        }
      }
    } finally {
      gates.Dispose();
    }
  }

  /// <summary>
  /// Takes a set-based group up to its apply: a rewind the cursor asks for and a stream that needs its bootstrap
  /// snapshot run as the per-pair path runs them (finished here, and false is returned); otherwise its events are read
  /// and it is queued for the batch (true). A runner that does not expose its read, or a group with no events to apply,
  /// runs the per-pair normal path here.
  /// </summary>
  private async Task<bool> _beginClaimedApplyAsync(ClaimedBatchContext batch, ClaimedGroup group, CancellationToken ct) {
    var checkpoint = group.Checkpoint;
    var cursorStatus = checkpoint?.Status ?? PerspectiveProcessingStatus.None;
    // A status that asks for a rewind comes only with a cursor.
    if (cursorStatus.HasFlag(PerspectiveProcessingStatus.RewindRequired) && checkpoint!.RewindTriggerEventId is not null) {
      // A rewind replays the stream on its own; it is the per-pair path's, as it always was.
      _markAffinityPhase(group.StreamId, group.PerspectiveName, "apply");
      var (result, processingMode, rewindLockSkipped) = await _executePerspectiveRunnerAsync(
        group.Work, group.Runner!, checkpoint, group.StreamContext, group.EnablePerspectiveSpans, ct).ConfigureAwait(false);
      if (!rewindLockSkipped) {
        await _finishClaimedGroupAsync(batch, group, result, processingMode, ct).ConfigureAwait(false);
      }
      return false;
    }

    await _bootstrapSnapshotIfNeededAsync(
      group.Runner!, group.StreamId, group.PerspectiveName, group.StreamContext.LastProcessedEventId, ct).ConfigureAwait(false);
    var events = group.Prefetched is { } prefetched && group.Runner!.SelectEvents(prefetched.StreamEvents) is { } selected
      ? selected
      : await group.Runner!.ReadEventsAsync(group.StreamId, group.StreamContext.LastProcessedEventId, ct).ConfigureAwait(false);
    if (events is not { Count: > 0 }) {
      _markAffinityPhase(group.StreamId, group.PerspectiveName, "apply");
      var result = await _executeNormalPathAsync(
        group.Runner, group.StreamId, group.PerspectiveName, group.StreamContext.LastProcessedEventId,
        group.EnablePerspectiveSpans, ct).ConfigureAwait(false);
      await _finishClaimedGroupAsync(batch, group, result, processingMode: null, ct).ConfigureAwait(false);
      return false;
    }
    group.BatchPair = new PerspectiveApplyBatch.Pair(
      group.StreamId, group.PerspectiveName, group.Runner, group.StreamContext.LastProcessedEventId, events);
    return true;
  }

  /// <summary>
  /// Reads the cursors of a claimed batch's set-based pairs with one statement, and their streams' events after them with
  /// one read (#2246), so each pair begins with what its own two reads would have returned. Null, and each pair reads its
  /// own, when there are no event types to read, the coordinator does not read cursors in batches, there is no event store
  /// or it does not read streams in batches, or a read fails (logged).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/PerspectiveWorkerDrainModeTests.SetBased.cs:SetBasedClaimed_ReadsEveryPairsCursorAndEvents_WithOneReadEachAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/PerspectiveWorkerDrainModeTests.SetBased.cs:SetBasedClaimed_ABatchedReadThatFails_LeavesEachPairToItsOwnReadsAsync</tests>
  private async Task<Dictionary<(Guid StreamId, string PerspectiveName), ClaimedPrefetch>?> _prefetchClaimedAsync(
      List<(Guid StreamId, string PerspectiveName)> pairs, CancellationToken ct) {
    var eventTypes = _eventTypeProvider.GetEventTypes();
    if (eventTypes.Count == 0) {
      return null;
    }
    var scope = _scopeFactory.CreateAsyncScope();
    await using (scope.ConfigureAwait(false)) {
      var coordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
      if (!coordinator.ReadsCursorsInBatches || scope.ServiceProvider.GetService<IEventStore>() is not { } eventStore) {
        return null;
      }
      try {
        var cursors = (await coordinator.GetPerspectiveCursorsBatchAsync([.. pairs.Select(p => p.StreamId).Distinct()], ct).ConfigureAwait(false))
          .ToDictionary(c => (c.StreamId, c.PerspectiveName));
        var read = await eventStore.ReadPolymorphicManyAsync(
          [.. pairs.Select(p => (p.StreamId, cursors.GetValueOrDefault(p)?.LastEventId))], eventTypes, ct).ConfigureAwait(false);
        if (read is null) {
          return null;
        }
        var prefetched = new Dictionary<(Guid StreamId, string PerspectiveName), ClaimedPrefetch>();
        foreach (var pair in pairs) {
          var checkpoint = cursors.GetValueOrDefault(pair);
          // The stream was read from its pairs' earliest cursor: each pair takes the events after its own.
          prefetched[pair] = new ClaimedPrefetch(checkpoint, [.. read.GetValueOrDefault(pair.StreamId, [])
            .Where(e => checkpoint?.LastEventId is not { } last || e.MessageId.Value.CompareTo(last) > 0)]);
        }
        return prefetched;
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogClaimedBatchReadFailed(_logger, ex, pairs.Count);
        return null;
      }
    }
  }

  [LoggerMessage(EventId = 82, Level = LogLevel.Warning,
    Message = "The claimed batch's one read of its {PairCount} set-based pair(s) failed; each pair reads its own cursor and events")]
  static partial void LogClaimedBatchReadFailed(ILogger logger, Exception exception, int pairCount);

  /// <summary>The services a set-based batch runs with, resolved for this batch.</summary>
  private PerspectiveApplyBatch.Services _batchServices() {
    var root = _scopeFactory.CreateScope();
    try {
      var provider = root.ServiceProvider;
      return new PerspectiveApplyBatch.Services(
        _scopeFactory,
        provider.GetService<IPerspectiveApplyCoordinator>(),
        provider.GetService<IPerspectiveStateSource>(),
        provider.GetService<IApplyBatchStrategy>() ?? _defaultFlushPolicy,
        _effectiveWidth(),
        _logger);
    } finally {
      root.Dispose();
    }
  }

  private static readonly IApplyBatchStrategy _defaultFlushPolicy = new FlushEveryBatchStrategy();

  /// <summary>
  /// Takes the affinity gates of a batch's pairs in <see cref="PerspectiveApplyLockSet.Order"/>, each pair once, so two
  /// batches that share pairs wait on the first pair they share and never each hold a gate the other needs. Disposing
  /// the result releases them in the reverse order. When one cannot be taken (the worker is stopping), the ones already
  /// held are released before the cancellation is thrown.
  /// </summary>
  /// <remarks>Internal so a test can hold it to its release on cancellation.</remarks>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/PerspectiveWorkerDrainModeTests.SetBased.cs:AffinityGates_TakenForABatch_AreReleased_WhenTakingOneIsCanceledAsync</tests>
  internal async Task<IDisposable> AcquireAffinityGatesInOrderAsync(
      IEnumerable<(Guid StreamId, string PerspectiveName)> pairs, string path, CancellationToken ct) {
    _ensureCursorCacheEvictionSubscribed();
    var held = new HeldAffinityGates(this);
    try {
      foreach (var (streamId, perspectiveName) in new SortedSet<(Guid StreamId, string PerspectiveName)>(pairs, PerspectiveApplyLockSet.Order)) {
        var entry = _getStreamAffinityGate(streamId, perspectiveName);
        await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        _markAffinityHeld(entry, path);
        held.Add(streamId, perspectiveName, entry);
      }
    } catch {
      held.Dispose();
      throw;
    }
    return held;
  }

  /// <summary>Gates taken by <see cref="AcquireAffinityGatesInOrderAsync"/>, released in the reverse order.</summary>
  private sealed class HeldAffinityGates(PerspectiveWorker worker) : IDisposable {
    private readonly List<(Guid StreamId, string PerspectiveName, StreamAffinityGateEntry Entry)> _held = [];

    public void Add(Guid streamId, string perspectiveName, StreamAffinityGateEntry entry) => _held.Add((streamId, perspectiveName, entry));

    // Disposed once: by the batch that took the gates, or by a failed acquisition before it is handed out.
    public void Dispose() {
      for (var i = _held.Count - 1; i >= 0; i--) {
        worker._releaseStreamAffinityGate(_held[i].StreamId, _held[i].PerspectiveName, _held[i].Entry, abandoned: false);
      }
    }
  }
}
