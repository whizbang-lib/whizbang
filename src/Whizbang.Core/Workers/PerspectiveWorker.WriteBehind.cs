// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Workers;

/// <summary>
/// The write-behind mode (#2247): a drain's set-based lap holds the folded models of its write-behind pairs instead of
/// writing them, and later flushes each model's batches with one merged write through the same set-based writer.
/// </summary>
/// <remarks>
/// <para>
/// A held pair is not finished: its completion, sync signal and post-perspective stages wait for its flush, and its work
/// rows stay pending and leased to this instance meanwhile. Every drain of the stream fetches them again until then and
/// leaves out the events a held model already folded. A crash loses the held models and nothing else; the rows are
/// claimed again after their lease and replay from the checkpoint, which never moved.
/// </para>
/// <para>
/// A model is flushed when its interval elapses, at the batch cap, when the cache is over a bound, when a sync wait waits
/// on one of its events, when anything else is about to touch its row (a rewind, a per-pair apply, a drain, a purge, a
/// failure), and at stop. A model whose lease has run out is dropped, never written: its stream may already be another
/// instance's. A writer outside the runner (a collective, a rebuild) is met by the version check of the merged write,
/// which then re-applies each held batch on the row it finds.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#write-behind</docs>
public partial class PerspectiveWorker {
  private PerspectiveWriteBehindCache<DrainPair>? _writeBehind;
  private ITimer? _writeBehindTimer;
  private int _writeBehindTicking;
  private Task _writeBehindTick = Task.CompletedTask;
  private readonly CancellationTokenSource _writeBehindStopping = new();
  private readonly ConcurrentDictionary<Task, byte> _writeBehindSyncFlushes = new();

  /// <summary>The timer's flush in flight, if any; read by tests that hold it off.</summary>
  internal Task WriteBehindTickInFlight => _writeBehindTick;

  /// <summary>The models the write-behind cache holds (#2247); zero when nothing writes behind.</summary>
  /// <docs>fundamentals/perspectives/write-modes#write-behind</docs>
  public int WriteBehindModels => _writeBehind?.Count ?? 0;

  /// <summary>
  /// Raised when a write-behind batch is held instead of written (#2247), with its (stream, perspective). A diagnostic
  /// signal: the batch's completion, sync signal and post-perspective stages follow its model's flush.
  /// </summary>
  /// <docs>fundamentals/perspectives/write-modes#write-behind</docs>
  public event Action<(Guid StreamId, string PerspectiveName)>? OnWriteBehindHeld;

  /// <summary>
  /// Flushes every model the write-behind cache holds now (#2247), whatever its interval: each one's batches are
  /// written, completed and finished before the returned task completes. Nothing to do when nothing writes behind.
  /// </summary>
  /// <param name="cancellationToken">Cancels the flush; a batch it did not complete replays from its checkpoint.</param>
  /// <returns>A task that completes once every held model is flushed.</returns>
  /// <docs>fundamentals/perspectives/write-modes#write-behind</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveWriteModeConformanceTests.WriteBehind.cs</tests>
  public Task FlushWriteBehindAsync(CancellationToken cancellationToken = default) =>
    _writeBehind is null
      ? Task.CompletedTask
      : _flushWriteBehindKeysAsync([.. _writeBehind.Keys().Select(k => (k, WriteBehindFlushReason.Requested))], cancellationToken);

  /// <summary>Whether a mode prepares its pairs in a set-based batch: set-based and write-behind both do.</summary>
  private static bool _isBatched(PerspectiveWriteMode mode) =>
    mode is PerspectiveWriteMode.SetBased or PerspectiveWriteMode.WriteBehind;

  /// <inheritdoc/>
  /// <remarks>
  /// Refuses to start when the service's write mode is write-behind without the cache's bounds (#2247). A perspective
  /// that declares write-behind is checked when the worker reads its perspectives, before it applies anything.
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/PerspectiveWorkerDrainModeTests.WriteBehind.cs:WriteBehind_TheServiceModeWithoutBounds_FailsToStartAsync</tests>
  public override Task StartAsync(CancellationToken cancellationToken) {
    _throwIfUnbounded([]);
    return base.StartAsync(cancellationToken);
  }

  /// <summary>
  /// Validates the bounds and, when anything this worker applies writes behind, creates the cache, its timer and its sync
  /// listener. Only this worker's perspectives count: a perspective another service declares is not this worker's.
  /// </summary>
  /// <param name="registered">The perspectives this worker applies.</param>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/PerspectiveWorkerDrainModeTests.WriteBehind.cs:WriteBehind_ADeclaredPerspectiveWithoutBounds_StopsTheWorkerBeforeItAppliesAnythingAsync</tests>
  private void _initWriteBehind(IEnumerable<string> registered) {
    var mine = registered.ToHashSet(StringComparer.Ordinal);
    var declared = PerspectiveWriteModes.DeclaredWith(PerspectiveWriteMode.WriteBehind).Where(mine.Contains).ToList();
    _throwIfUnbounded(declared);
    if (_writeBehind is not null || (_options.WriteMode != PerspectiveWriteMode.WriteBehind && declared.Count == 0)) {
      return;
    }
    var leaseLimit = TimeSpan.FromSeconds(_leaseRenewalOptions.LeaseSeconds - _leaseHandleOptions.LeaseGraceSeconds);
    _writeBehind = new PerspectiveWriteBehindCache<DrainPair>(_options.WriteBehind, leaseLimit);
    var period = TimeSpan.FromMilliseconds(Math.Max(1, _options.WriteBehind.MaxFlushIntervalMilliseconds / 4));
    _writeBehindTimer = _timeProvider.CreateTimer(_ => _onWriteBehindTick(), null, period, period);
    if (_syncEventTracker is Whizbang.Core.Perspectives.Sync.ISyncWaitAnnouncer announcer) {
      announcer.WaitStarted += _onSyncWaitStarted;
    }
  }

  private void _throwIfUnbounded(IReadOnlyList<string> declared) {
    if (PerspectiveWriteBehindOptions.Validate(
          _options.WriteBehind, _options.WriteMode, declared,
          _leaseRenewalOptions.LeaseSeconds, _leaseHandleOptions.LeaseGraceSeconds) is { } error) {
      throw new InvalidOperationException(error);
    }
  }

  /// <summary>A timer tick: flushes what is due, unless the previous tick is still flushing.</summary>
  private void _onWriteBehindTick() {
    if (Interlocked.Exchange(ref _writeBehindTicking, 1) == 1) {
      return;
    }
    _writeBehindTick = _runWriteBehindTickAsync();
  }

  private async Task _runWriteBehindTickAsync() {
    try {
      await FlushWriteBehindDueAsync(_writeBehindStopping.Token).ConfigureAwait(false);
    } catch (OperationCanceledException) when (_writeBehindStopping.IsCancellationRequested) {
      // Stopping: the stop flushes what is left.
    } catch (Exception ex) {
      LogWriteBehindFlushFailed(_logger, ex);
    } finally {
      Volatile.Write(ref _writeBehindTicking, 0);
    }
  }

  /// <summary>A sync wait began: flushes at once every held model holding one of its events.</summary>
  private void _onSyncWaitStarted(IReadOnlyList<Guid> eventIds) {
    var keys = _writeBehind?.KeysHolding([.. eventIds]);
    if (keys is not { Count: > 0 }) {
      return;
    }
    var flush = _flushWriteBehindKeysAsync([.. keys.Select(k => (k, WriteBehindFlushReason.Sync))], _writeBehindStopping.Token);
    _writeBehindSyncFlushes.TryAdd(flush, 0);
    _ = flush.ContinueWith(t => {
      _writeBehindSyncFlushes.TryRemove(t, out _);
      if (t.Exception is { } ex) {
        LogWriteBehindFlushFailed(_logger, ex.GetBaseException());
      }
    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
  }

  /// <summary>Stops the timer and the sync listener, waits for flushes in flight, and flushes every held model.</summary>
  private async Task _stopWriteBehindAsync(CancellationToken cancellationToken) {
    if (_writeBehind is null) {
      _writeBehindStopping.Dispose();
      return;
    }
    if (_syncEventTracker is Whizbang.Core.Perspectives.Sync.ISyncWaitAnnouncer announcer) {
      announcer.WaitStarted -= _onSyncWaitStarted;
    }
    if (_writeBehindTimer is not null) {
      await _writeBehindTimer.DisposeAsync().ConfigureAwait(false);
    }
    await _writeBehindTick.ConfigureAwait(false);
    await Task.WhenAll(_writeBehindSyncFlushes.Keys).ConfigureAwait(false);
    // What outlived its lease is dropped, never written, at stop as at any other time.
    await FlushWriteBehindDueAsync(cancellationToken).ConfigureAwait(false);
    await _flushWriteBehindKeysAsync(
      [.. _writeBehind.Keys().Select(k => (k, WriteBehindFlushReason.Stop))], cancellationToken).ConfigureAwait(false);
    await _writeBehindStopping.CancelAsync().ConfigureAwait(false);
    _writeBehindStopping.Dispose();
  }

  /// <summary>Flushes what the cache has due, and drops the models whose lease has run out.</summary>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/PerspectiveWorkerDrainModeTests.WriteBehind.cs</tests>
  internal async Task FlushWriteBehindDueAsync(CancellationToken cancellationToken) {
    if (_writeBehind is null) {
      return;
    }
    var due = _writeBehind.Due(_timeProvider.GetUtcNow());
    // An expired model goes through the flush too, which drops what has outlived its lease rather than write it.
    await _flushWriteBehindKeysAsync(
      [.. due.Flush, .. due.Expired.Select(k => (k, WriteBehindFlushReason.Interval))], cancellationToken).ConfigureAwait(false);
    _reportWriteBehindSize();
  }

  private const string WRITE_BEHIND_PATH = "write-behind";

  /// <summary>Flushes the held models of <paramref name="keys"/>, taking their affinity gates first.</summary>
  private async Task _flushWriteBehindKeysAsync(
      IReadOnlyList<((Guid StreamId, string PerspectiveName) Key, WriteBehindFlushReason Reason)> keys, CancellationToken cancellationToken) {
    if (keys.Count == 0) {
      return;
    }
    using var gates = await AcquireAffinityGatesInOrderAsync(keys.Select(k => k.Key), WRITE_BEHIND_PATH, cancellationToken).ConfigureAwait(false);
    await _flushWriteBehindLockedAsync(keys, takeApplyLocks: true, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Flushes the held models of <paramref name="keys"/> that are cached, for one reason, under the caller's gates.</summary>
  private Task _flushWriteBehindHeldAsync(
      IEnumerable<(Guid StreamId, string PerspectiveName)> keys, bool takeApplyLocks, CancellationToken cancellationToken) {
    if (_writeBehind is null) {
      return Task.CompletedTask;
    }
    var cached = keys.Distinct().Where(k => _writeBehind.Get(k) is not null)
      .Select(k => (k, WriteBehindFlushReason.Bypass)).ToList();
    return cached.Count == 0
      ? Task.CompletedTask
      : _flushWriteBehindLockedAsync(cached, takeApplyLocks, cancellationToken);
  }

  /// <summary>
  /// The flush, under the affinity gates of <paramref name="keys"/>, which the caller holds: each model's batches are
  /// written with one merged write, each through its context's sink and the flush policy; every held batch is then
  /// completed with that write's outcome, oldest first, and finished as the drain finishes a pair.
  /// </summary>
  /// <remarks>
  /// A merged write that did not land (the row moved, the ordering guard refused it, the database rejected it) hands
  /// every held batch to the per-pair path, oldest first, where each is re-applied on the row it finds.
  /// </remarks>
  private async Task _flushWriteBehindLockedAsync(
      IReadOnlyList<((Guid StreamId, string PerspectiveName) Key, WriteBehindFlushReason Reason)> keys,
      bool takeApplyLocks,
      CancellationToken cancellationToken) {
    var taken = keys
      .Select(k => (k.Key, k.Reason, Entry: _writeBehind!.Take(k.Key)))
      .Where(e => e.Entry is not null)
      .ToList();
    // Checked once the model is taken, under its gate: a flush that waited for the gate may find its lease run out, and
    // a model whose rows may already be another instance's is dropped, never written.
    var now = _timeProvider.GetUtcNow();
    foreach (var (key, _, entry) in taken.Where(e => _writeBehind!.HasOutlivedLease(e.Entry!, now))) {
      await _dropWriteBehindAsync(key, entry!).ConfigureAwait(false);
    }
    var entries = taken.Where(e => !_writeBehind!.HasOutlivedLease(e.Entry!, now)).ToList();
    if (entries.Count == 0) {
      _reportWriteBehindSize();
      return;
    }
    try {
      await _writeAndCompleteAsync(entries, takeApplyLocks, cancellationToken).ConfigureAwait(false);
    } finally {
      // Whatever ended the flush early (the worker stopping, a lock it could not take), every held batch not completed
      // is released, and its rows replay from the checkpoint, which never moved.
      foreach (var pair in entries.SelectMany(e => e.Entry!.Holds).Select(h => h.Item).Where(p => !p.Released)) {
        await _releaseHeldAsync(pair).ConfigureAwait(false);
      }
      _reportWriteBehindSize();
    }
  }

  private async Task _writeAndCompleteAsync(
      List<((Guid StreamId, string PerspectiveName) Key, WriteBehindFlushReason Reason, WriteBehindEntry<DrainPair>? Entry)> entries,
      bool takeApplyLocks,
      CancellationToken cancellationToken) {
    var services = _batchServices();
    await using var locks = takeApplyLocks
      ? await PerspectiveApplyLockSet.AcquireAsync(services.Coordinator, entries.Select(e => e.Key), cancellationToken).ConfigureAwait(false)
      : null;
    var now = _timeProvider.GetUtcNow();
    var outcomes = await PerspectiveApplyBatch.WriteAsync(
      [.. entries.Select(e => e.Entry!.MergedWrite())], services, cancellationToken).ConfigureAwait(false);
    // The batches that held the pairs have ended: the flush gathers their processed events for the post-perspective stages.
    var processed = new ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)>();
    var isNew = new ConcurrentDictionary<Guid, bool>();
    var parallel = new ParallelOptions { MaxDegreeOfParallelism = _effectiveWidth(), CancellationToken = cancellationToken };
    await Parallel.ForEachAsync(Enumerable.Range(0, entries.Count), parallel, async (i, ct) => {
      var (_, reason, held) = entries[i];
      var entry = held!;
      // Recorded when the merged write has landed, before any held batch reports its completion.
      var tag = new KeyValuePair<string, object?>("reason", _reasonTag(reason));
      _metrics?.WriteBehindFlushLag.Record((now - entry.FirstHeldAt).TotalMilliseconds, tag);
      if (reason is not WriteBehindFlushReason.Interval) {
        _metrics?.WriteBehindForcedFlushes.Add(1, tag);
      }
      var outcome = _heldOutcome(outcomes[i]);
      foreach (var pair in entry.Holds.Select(h => h.Item)) {
        pair.BatchContext = pair.BatchContext with { BatchProcessedEvents = processed, BatchIsNewByEventId = isNew };
        await _completeHeldAsync(pair, outcome, ct).ConfigureAwait(false);
      }
    }).ConfigureAwait(false);
    await _startBatchPostLifecycleAsync(
      processed, [], isNew, entries[0].Entry!.Holds[0].Item.BatchContext.LifecycleCoordinator, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>One held batch after its model's flush: completed with the outcome, finished or failed, and released.</summary>
  private async Task _completeHeldAsync(DrainPair pair, PerspectiveWriteOutcome outcome, CancellationToken ct) {
    try {
      var batchPair = pair.BatchPair!;
      try {
        batchPair.Result = await batchPair.Runner.CompleteAsync(batchPair.Prepared!, outcome, ct).ConfigureAwait(false);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        batchPair.Failure = ex;
      }
      using var lease = _drainLease(pair.StreamId, ct);
      await _endDrainBatchPairAsync(pair, lease.Token, ct).ConfigureAwait(false);
    } finally {
      await _releaseHeldAsync(pair).ConfigureAwait(false);
    }
  }

  /// <summary>Ends a held batch's hold: its scope released, once.</summary>
  private static async Task _releaseHeldAsync(DrainPair pair) {
    if (pair.Released) {
      return;
    }
    pair.Released = true;
    await pair.ScopeLifetime.DisposeAsync().ConfigureAwait(false);
  }

  /// <summary>
  /// Drops a held model whose lease has run out, unwritten (#2247): its stream may already be another instance's, so this
  /// one must not write it. Its rows replay from the checkpoint, which never moved, wherever they are claimed next.
  /// </summary>
  private async Task _dropWriteBehindAsync((Guid StreamId, string PerspectiveName) key, WriteBehindEntry<DrainPair> entry) {
    LogWriteBehindDropped(_logger, key.PerspectiveName, key.StreamId, entry.Holds.Count);
    _metrics?.WriteBehindDropped.Add(1);
    foreach (var pair in entry.Holds.Select(h => h.Item)) {
      await _releaseHeldAsync(pair).ConfigureAwait(false);
    }
  }

  /// <summary>The outcome each held batch is completed with: written, or handed to the per-pair path.</summary>
  private static PerspectiveWriteOutcome _heldOutcome(PerspectiveWriteOutcome merged) =>
    merged.Status == PerspectiveWriteStatus.Refused
      // The guard turned the merged write away: each batch is re-applied per pair, where the guard rewinds its stream.
      ? PerspectiveWriteOutcome.Conflict(PerspectiveRowVersion.Unchecked)
      : merged;

  private static string _reasonTag(WriteBehindFlushReason reason) => reason switch {
    WriteBehindFlushReason.Interval => "interval",
    WriteBehindFlushReason.Batches => "batches",
    WriteBehindFlushReason.Bounds => "bounds",
    WriteBehindFlushReason.Bypass => "bypass",
    WriteBehindFlushReason.Sync => "sync",
    WriteBehindFlushReason.Requested => "requested",
    _ => "stop",
  };

  private void _reportWriteBehindSize() => _metrics?.SetWriteBehindSize(_writeBehind!.Count, _writeBehind.Bytes);

  /// <summary>The hooks a drain's set-based lap gives <see cref="PerspectiveApplyBatch"/> when a perspective writes behind.</summary>
  private sealed class WriteBehindHooks(PerspectiveWorker worker, IReadOnlyDictionary<PerspectiveApplyBatch.Pair, DrainPair> pairs)
      : PerspectiveApplyBatch.IWriteBehindHooks {
    public IPerspectiveBatchState? Wrap(IPerspectiveBatchState? inner) => new WriteBehindBatchState(
      inner,
      (streamId, perspectiveName) => worker._writeBehind!.Get((streamId, perspectiveName))?.Row,
      perspectiveName => worker._writeModeOf(perspectiveName) == PerspectiveWriteMode.WriteBehind,
      (perspectiveName, hit) => worker._metrics?.WriteBehindLookups.Add(1,
        new KeyValuePair<string, object?>(METRIC_TAG_PERSPECTIVE_NAME, perspectiveName),
        new KeyValuePair<string, object?>("result", hit ? "hit" : "miss")));

    public bool TryHold(PerspectiveApplyBatch.Pair pair) {
      var write = pair.Prepared!.Write!;
      // Only a write checked against the row's version can be held: the merged write must meet any writer it missed.
      if (!write.ExpectedVersion.IsChecked) {
        return false;
      }
      var hold = new WriteBehindHold<DrainPair>(
        pairs[pair], write, pair.Prepared.WriteBehindRow!, [.. pair.Events.Select(e => e.MessageId.Value)]);
      if (!worker._writeBehind!.TryHold((pair.StreamId, pair.PerspectiveName), hold, worker._timeProvider.GetUtcNow())) {
        return false;
      }
      worker.OnWriteBehindHeld?.Invoke((pair.StreamId, pair.PerspectiveName));
      return true;
    }

    public Task FlushBeforeWriteAsync(IReadOnlyList<PerspectiveApplyBatch.Pair> written, CancellationToken cancellationToken) =>
      worker._flushWriteBehindHeldAsync(written.Select(p => (p.StreamId, p.PerspectiveName)), takeApplyLocks: false, cancellationToken);
  }

  [LoggerMessage(EventId = 90, Level = LogLevel.Warning,
    Message = "Write-behind perspective flush failed; the batches it held were released unwritten and replay from their checkpoints")]
  static partial void LogWriteBehindFlushFailed(ILogger logger, Exception exception);

  [LoggerMessage(EventId = 91, Level = LogLevel.Warning,
    Message = "Write-behind model of {PerspectiveName} on stream {StreamId} outlived its lease and is dropped unwritten; its {BatchCount} held batch(es) replay from the checkpoint wherever the stream is claimed next")]
  static partial void LogWriteBehindDropped(ILogger logger, string perspectiveName, Guid streamId, int batchCount);
}
