using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
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
    return await _rebuildCoreAsync(perspectiveName, RebuildMode.BlueGreen, streamIds: null, ct);
  }

  /// <inheritdoc/>
  public async Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) {
    return await _rebuildCoreAsync(perspectiveName, RebuildMode.InPlace, streamIds: null, ct);
  }

  /// <inheritdoc/>
  public async Task<RebuildResult> RebuildStreamsAsync(
      string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) {
    var ids = streamIds.ToList();
    return await _rebuildCoreAsync(perspectiveName, RebuildMode.SelectedStreams, ids, ct);
  }

  /// <inheritdoc/>
  public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) {
    _activeRebuilds.TryGetValue(perspectiveName, out var status);
    return Task.FromResult(status);
  }

  private async Task<RebuildResult> _rebuildCoreAsync(
      string perspectiveName, RebuildMode mode, List<Guid>? streamIds, CancellationToken ct) {

    var sw = Stopwatch.StartNew();
    int streamsProcessed = 0;
    int eventsReplayed = 0;

    try {
      await using var scope = scopeFactory.CreateAsyncScope();
      var sp = scope.ServiceProvider;

      var registry = sp.GetRequiredService<IPerspectiveRunnerRegistry>();
      var runner = registry.GetRunner(perspectiveName, sp);

      if (runner == null) {
        var registered = string.Join(", ", registry.GetRegisteredPerspectives().Select(p => p.ClrTypeName));
        return new RebuildResult(perspectiveName, 0, 0, sw.Elapsed, false,
            $"No runner found for perspective '{perspectiveName}'. Registered: {registered}");
      }

      // Cursor persistence: rebuild captures each runner.RunAsync return value and flushes
      // them through IPerspectiveCheckpointCompleter so wh_perspective_cursors reflects the
      // rebuild end-state. Optional dependency — when no driver registers a completer, the
      // rebuilder still updates projections and just skips cursor persistence.
      var completer = sp.GetService<IPerspectiveCheckpointCompleter>();
      var pendingCompletions = completer != null ? new List<PerspectiveCursorCompletion>(64) : null;

      // When the caller didn't supply an explicit list, narrow to streams that actually contain
      // events this perspective handles — otherwise we'd iterate every stream for every
      // perspective (most RunAsync calls become no-ops).
      streamIds ??= await _resolveStreamIdsToReplayAsync(sp, registry, perspectiveName, ct);

      var totalStreams = streamIds.Count;

      // Track active rebuild status
      var status = new RebuildStatus(perspectiveName, mode, totalStreams, 0, DateTimeOffset.UtcNow);
      _activeRebuilds[perspectiveName] = status;

      LogRebuildStarting(logger, mode, perspectiveName, totalStreams, completer != null);

      // Set ambient processing mode so lifecycle receptors are suppressed during rebuild
      // unless they opt in with [FireDuringReplay]
      var previousMode = Current;
      Current = ProcessingMode.Rebuild;
      try {
        foreach (var streamId in streamIds) {
          ct.ThrowIfCancellationRequested();
          (streamsProcessed, eventsReplayed) = await _replayStreamAsync(
            runner, perspectiveName, streamId, completer, pendingCompletions,
            status, streamsProcessed, eventsReplayed, totalStreams, sw, ct);
        }

        await _flushPendingCompletionsAsync(completer, pendingCompletions, perspectiveName, streamsProcessed, totalStreams, ct);
      } finally {
        Current = previousMode;
      }

      sw.Stop();
      LogRebuildCompleted(logger, mode, perspectiveName, streamsProcessed, sw.ElapsedMilliseconds);

      return new RebuildResult(perspectiveName, streamsProcessed, eventsReplayed, sw.Elapsed, true, null);
    } catch (Exception ex) {
      sw.Stop();
      LogRebuildFailed(logger, ex, mode, perspectiveName, streamsProcessed, sw.ElapsedMilliseconds);
      return new RebuildResult(perspectiveName, streamsProcessed, eventsReplayed, sw.Elapsed, false, ex.Message);
    } finally {
      _activeRebuilds.TryRemove(perspectiveName, out _);
    }
  }

  /// <summary>
  /// Narrows rebuild scope to streams that actually contain events this perspective handles,
  /// falling back to every stream when the perspective's event-type metadata is missing (rare —
  /// incomplete registration). The source-generated registry supplies the event-type set.
  /// </summary>
  private async Task<List<Guid>> _resolveStreamIdsToReplayAsync(
      IServiceProvider sp, IPerspectiveRunnerRegistry registry, string perspectiveName, CancellationToken ct) {
    var eventStoreQuery = sp.GetRequiredService<IEventStoreQuery>();
    var perspectiveInfo = registry.GetRegisteredPerspectives()
        .FirstOrDefault(p => p.ClrTypeName == perspectiveName);
    var relevantEventTypes = perspectiveInfo?.EventTypes;

    if (relevantEventTypes is { Count: > 0 }) {
      // A type-change upcaster (LegacyA → GenericB) lets a stream that only carries the legacy
      // input feed this perspective on rebuild — but such a stream has none of the perspective's
      // own subscribed types, so the scoping below would skip it. Widen the scope with those
      // upcasters' source-type names (scoped to upcasters whose target this perspective subscribes
      // to). Empty for the common no-type-change case, so the query shape is unchanged there.
      IReadOnlyList<string> effectiveEventTypes = relevantEventTypes;
      var pipeline = sp.GetService<EventUpcasterPipeline>();
      if (pipeline is not null) {
        var extraNames = pipeline.ExtraInputTypeNamesFor(relevantEventTypes);
        if (extraNames.Count > 0) {
          var widened = new List<string>(relevantEventTypes);
          widened.AddRange(extraNames);
          effectiveEventTypes = widened;
        }
      }

      var streamIds = await eventStoreQuery.Query
          .Where(e => effectiveEventTypes.Contains(e.EventType))
          .Select(e => e.StreamId)
          .Distinct()
          .ToListAsync(ct);
      LogStreamScoping(logger, perspectiveName, streamIds.Count, effectiveEventTypes.Count);
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
  private async Task<(int StreamsProcessed, int EventsReplayed)> _replayStreamAsync(
      IPerspectiveRunner runner,
      string perspectiveName,
      Guid streamId,
      IPerspectiveCheckpointCompleter? completer,
      List<PerspectiveCursorCompletion>? pendingCompletions,
      RebuildStatus status,
      int streamsProcessed,
      int eventsReplayed,
      int totalStreams,
      Stopwatch overallSw,
      CancellationToken ct) {
    var streamSw = Stopwatch.StartNew();
    try {
      // RunRebuildAsync replays the physical stream and returns one completion per TARGET stream.
      // Without re-key upcasters this is a single completion equal to streamId (identical to the
      // old RunAsync path); with a re-key upcaster, events fan out onto their target rows and a
      // cursor is queued for each. streamsProcessed counts physical streams (one per call) so the
      // rebuild result stays keyed to the resolved stream list.
      var completions = await runner.RunRebuildAsync(streamId, perspectiveName, ct);
      streamSw.Stop();
      streamsProcessed++;
      eventsReplayed += completions.Count;

      foreach (var completion in completions) {
        LogStreamReplayed(logger, perspectiveName, completion.StreamId, completion.LastEventId,
            completion.Status, streamSw.ElapsedMilliseconds, streamsProcessed, totalStreams);

        if (pendingCompletions != null) {
          pendingCompletions.Add(completion);
          if (pendingCompletions.Count >= COMPLETION_FLUSH_BATCH_SIZE) {
            await _flushPendingCompletionsAsync(completer, pendingCompletions, perspectiveName, streamsProcessed, totalStreams, ct);
          }
        }
      }

      if (streamsProcessed % 100 == 0 || streamsProcessed == totalStreams) {
        _activeRebuilds[perspectiveName] = status with { ProcessedStreams = streamsProcessed };
        LogRebuildProgress(logger, perspectiveName, streamsProcessed, totalStreams,
            overallSw.ElapsedMilliseconds);
      }
    } catch (Exception ex) {
      streamSw.Stop();
      LogStreamFailed(logger, ex, perspectiveName, streamId, streamsProcessed, totalStreams);
    }
    return (streamsProcessed, eventsReplayed);
  }

  /// <summary>Flushes any queued cursor completions through the optional
  /// <see cref="IPerspectiveCheckpointCompleter"/> and logs the batch size + flush time.
  /// No-op when the completer or pending list is null/empty.</summary>
  private async Task _flushPendingCompletionsAsync(
      IPerspectiveCheckpointCompleter? completer,
      List<PerspectiveCursorCompletion>? pendingCompletions,
      string perspectiveName,
      int streamsProcessed,
      int totalStreams,
      CancellationToken ct) {
    if (completer is null || pendingCompletions is not { Count: > 0 }) {
      return;
    }
    var flushSw = Stopwatch.StartNew();
    var flushCount = pendingCompletions.Count;
    await completer.CompleteAsync(pendingCompletions, ct);
    flushSw.Stop();
    LogCursorFlushed(logger, perspectiveName, flushCount, flushSw.ElapsedMilliseconds,
        streamsProcessed, totalStreams);
    pendingCompletions.Clear();
  }

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Starting {Mode} rebuild of perspective {Perspective} — {StreamCount} streams; cursor persistence enabled={CursorPersistence}")]
  private static partial void LogRebuildStarting(ILogger logger, RebuildMode mode, string perspective, int streamCount, bool cursorPersistence);

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Completed {Mode} rebuild of perspective {Perspective} — {Streams} streams in {ElapsedMs}ms")]
  private static partial void LogRebuildCompleted(ILogger logger, RebuildMode mode, string perspective, int streams, long elapsedMs);

  [LoggerMessage(Level = LogLevel.Error,
      Message = "Failed {Mode} rebuild of perspective {Perspective} after {Streams} streams in {ElapsedMs}ms")]
  private static partial void LogRebuildFailed(ILogger logger, Exception ex, RebuildMode mode, string perspective, int streams, long elapsedMs);

  [LoggerMessage(Level = LogLevel.Warning,
      Message = "Rebuild {Perspective}: failed on stream {StreamId} ({Processed}/{Total})")]
  private static partial void LogStreamFailed(ILogger logger, Exception ex, string perspective, Guid streamId, int processed, int total);

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
