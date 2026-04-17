using System.Collections.Concurrent;
using System.Diagnostics;
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

      // Get stream IDs to process
      if (streamIds == null) {
        var eventStoreQuery = sp.GetRequiredService<IEventStoreQuery>();
        streamIds = await eventStoreQuery.Query
            .Select(e => e.StreamId)
            .Distinct()
            .ToListAsync(ct);
      }

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
        // Process each stream
        foreach (var streamId in streamIds) {
          ct.ThrowIfCancellationRequested();

          var streamSw = Stopwatch.StartNew();
          try {
            var completion = await runner.RunAsync(streamId, perspectiveName, null, ct);
            streamSw.Stop();
            streamsProcessed++;
            eventsReplayed++;

            LogStreamReplayed(logger, perspectiveName, streamId, completion.LastEventId,
                completion.Status, streamSw.ElapsedMilliseconds, streamsProcessed, totalStreams);

            if (pendingCompletions != null) {
              pendingCompletions.Add(completion);
              if (pendingCompletions.Count >= COMPLETION_FLUSH_BATCH_SIZE) {
                var flushSw = Stopwatch.StartNew();
                var flushCount = pendingCompletions.Count;
                await completer!.CompleteAsync(pendingCompletions, ct);
                flushSw.Stop();
                LogCursorFlushed(logger, perspectiveName, flushCount, flushSw.ElapsedMilliseconds,
                    streamsProcessed, totalStreams);
                pendingCompletions.Clear();
              }
            }

            if (streamsProcessed % 100 == 0 || streamsProcessed == totalStreams) {
              _activeRebuilds[perspectiveName] = status with { ProcessedStreams = streamsProcessed };
              LogRebuildProgress(logger, perspectiveName, streamsProcessed, totalStreams,
                  sw.ElapsedMilliseconds);
            }
          } catch (Exception ex) {
            streamSw.Stop();
            LogStreamFailed(logger, ex, perspectiveName, streamId, streamsProcessed, totalStreams);
          }
        }

        // Flush any remaining completions at the end of the rebuild.
        if (pendingCompletions is { Count: > 0 }) {
          var flushSw = Stopwatch.StartNew();
          var flushCount = pendingCompletions.Count;
          await completer!.CompleteAsync(pendingCompletions, ct);
          flushSw.Stop();
          LogCursorFlushed(logger, perspectiveName, flushCount, flushSw.ElapsedMilliseconds,
              streamsProcessed, totalStreams);
          pendingCompletions.Clear();
        }
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
