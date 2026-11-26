// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tracing;

namespace Whizbang.Core.Workers;

/// <summary>
/// #2601: the flagged rewinds a drain pair can never ask for. A pair forms only for an event its perspective applies, and a
/// flagged rewind ran only from a pair, so a cursor flagged from an event its perspective does not read (a type this
/// service does not register, or one it registers that the perspective does not apply) was never rewound: its queued row
/// was leased on every pass, nothing was recorded against it, and the cursor stayed flagged.
/// </summary>
public partial class PerspectiveWorker {
  /// <summary>
  /// Rewinds every (stream, perspective) whose fetched rows hold no event its perspective applies and whose stored cursor
  /// is flagged RewindRequired, from the stored trigger, and completes those rows when the rewind completes. A rewind that
  /// throws fails the rows (the bounded retry and the dead-letter bound apply); one that does not complete leaves them for
  /// the next lease. A perspective with no runner here is left to the unregistered-perspective cleanup.
  /// </summary>
  /// <remarks>
  /// The common case costs one lookup per row: rows whose perspective applies their event are skipped, and only the rest
  /// read their stored cursors (one batch read), so only a flagged cursor takes this path. An unflagged row of a type the
  /// perspective does not read is left as before: a newer instance may read it during a rolling deploy.
  /// </remarks>
  /// <docs>fundamentals/perspectives/rewind#a-flagged-rewind-with-nothing-to-apply</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/PerspectiveWorkerDeepPathDrainTests.FlaggedWithNothingToApply.cs</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReapedBodyBatchIsolationTests.TriggerThePerspectiveDoesNotRead.cs:ARewindFromAnEventThePerspectiveDoesNotRead_Completes_AndTheCursorIsNoLongerFlaggedAsync</tests>
  private async Task _rewindFlaggedPairsWithNothingToApplyAsync(
      IWorkCoordinator workCoordinator, List<StreamEventData> rows, List<MessageEnvelope<IEvent>> typedEvents,
      IReadOnlyDictionary<string, IReadOnlyList<string>> perspectivesPerEventType, CancellationToken ct) {
    var unapplied = RowsTheirPerspectiveDoesNotApply(rows, typedEvents, perspectivesPerEventType);
    if (unapplied.Count == 0) {
      return;
    }
    Guid[] streams = [.. unapplied.Keys.Select(k => k.StreamId).Distinct()];
    var cursors = await workCoordinator.GetPerspectiveCursorsBatchAsync(streams, ct).ConfigureAwait(false);
    foreach (var cursor in cursors) {
      if (cursor.Status.HasFlag(PerspectiveProcessingStatus.RewindRequired)
          && cursor.RewindTriggerEventId is Guid trigger
          && unapplied.TryGetValue((cursor.StreamId, cursor.PerspectiveName), out var pairRows)) {
        await _rewindFlaggedPairAsync(cursor.StreamId, cursor.PerspectiveName, trigger, pairRows, ct).ConfigureAwait(false);
      }
    }
  }

  /// <summary>
  /// The fetched rows whose perspective applies none of the batch's typed events for them, by (stream, perspective): an
  /// event the store did not read (a type this service does not register) or one whose type the perspective does not
  /// apply. The collective sink's rows are its own path's.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/PerspectiveWorkerDeepPathDrainTests.FlaggedWithNothingToApply.cs</tests>
  internal static Dictionary<(Guid StreamId, string PerspectiveName), List<StreamEventData>> RowsTheirPerspectiveDoesNotApply(
      List<StreamEventData> rows, List<MessageEnvelope<IEvent>> typedEvents,
      IReadOnlyDictionary<string, IReadOnlyList<string>> perspectivesPerEventType) {
    var typeKeyById = new Dictionary<Guid, string>(typedEvents.Count);
    var keyByType = new Dictionary<Type, string>();
    foreach (var envelope in typedEvents) {
      var type = envelope.Payload.GetType();
      if (!keyByType.TryGetValue(type, out var key)) {
        keyByType[type] = key = TypeNameFormatter.Format(type);
      }
      typeKeyById.TryAdd(envelope.MessageId.Value, key);
    }
    var unapplied = new Dictionary<(Guid StreamId, string PerspectiveName), List<StreamEventData>>();
    foreach (var row in rows) {
      if (row.PerspectiveName is not { } perspective
          || string.Equals(perspective, CollectiveRouting.SINK_PERSPECTIVE_NAME, StringComparison.Ordinal)
          || (typeKeyById.TryGetValue(row.EventId, out var typeKey)
              && PerspectivesFor(perspectivesPerEventType, typeKey).Contains(perspective))) {
        continue;
      }
      if (!unapplied.TryGetValue((row.StreamId, perspective), out var pairRows)) {
        unapplied[(row.StreamId, perspective)] = pairRows = [];
      }
      pairRows.Add(row);
    }
    return unapplied;
  }

  /// <summary>One flagged pair's rewind, in its own scope and under its affinity gate, as a drain pair's rewind runs.</summary>
  private async Task _rewindFlaggedPairAsync(
      Guid streamId, string perspectiveName, Guid trigger, List<StreamEventData> rows, CancellationToken ct) {
    await using var scope = _scopeFactory.CreateAsyncScope();
    // The registry is there: this path runs only once the worker has read its perspectives from it.
    var runner = scope.ServiceProvider.GetRequiredService<IPerspectiveRunnerRegistry>().GetRunner(perspectiveName, scope.ServiceProvider);
    if (runner is null) {
      return;
    }
    var workCoordinator = scope.ServiceProvider.GetRequiredService<IWorkCoordinator>();
    Guid[] workIds = [.. rows.Select(r => r.EventWorkId)];
    LogFlaggedRewindWithNothingToApply(_logger, streamId, perspectiveName, trigger, workIds.Length);
    await WithStreamAffinityGateAsync(streamId, perspectiveName, async _ => {
      _cursorCache.Invalidate(streamId, perspectiveName);
      PerspectiveCursorCompletion result;
      try {
        result = await _rewindAndCountAsync(
          runner,
          new RewindRequest(streamId, perspectiveName, trigger, TriggerCommitSequence: null, EventsBehind: null,
            scope.ServiceProvider.GetService<IEventStore>()),
          _tracingOptions.CurrentValue.IsEnabled(TraceComponents.Perspectives),
          ct).ConfigureAwait(false);
      } catch (Exception ex) when (ex is not OperationCanceledException) {
        LogRewindFailed(_logger, ex, perspectiveName, streamId, trigger);
        await _parkLeasedRowsAsync(workIds, ex.Message, MessageFailureReason.Unknown, ct).ConfigureAwait(false);
        return;
      }
      await _completionStrategy.ReportCompletionAsync(result, workCoordinator, ct).ConfigureAwait(false);
      if (result.Status == PerspectiveProcessingStatus.Completed) {
        foreach (var workId in workIds) {
          await _perspectiveCompletionChannel.EnqueueEventWorkIdAsync(workId, ct).ConfigureAwait(false);
        }
      }
    }, ct).ConfigureAwait(false);
  }
}
