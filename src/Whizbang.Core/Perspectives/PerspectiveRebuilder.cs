using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Implements perspective rebuild operations in multiple modes.
/// Used internally by the migration system and available to developers for operational needs.
/// Resolves runners from IPerspectiveRunnerRegistry, queries streams from IEventStoreQuery,
/// and replays events through IPerspectiveRunner.RunAsync.
/// </summary>
/// <docs>core-concepts/perspectives#rebuild</docs>
public sealed partial class PerspectiveRebuilder(
    IServiceScopeFactory scopeFactory,
    ILogger<PerspectiveRebuilder> logger) : IPerspectiveRebuilder {

  private readonly ConcurrentDictionary<string, RebuildStatus> _activeRebuilds = new();

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

      LogRebuildStarting(logger, mode, perspectiveName, totalStreams);

      // Process each stream
      foreach (var streamId in streamIds) {
        ct.ThrowIfCancellationRequested();

        try {
          var completion = await runner.RunAsync(streamId, perspectiveName, null, ct);
          streamsProcessed++;
          eventsReplayed++;

          if (streamsProcessed % 100 == 0 || streamsProcessed == totalStreams) {
            _activeRebuilds[perspectiveName] = status with { ProcessedStreams = streamsProcessed };
          }
        } catch (Exception ex) {
          LogStreamFailed(logger, ex, perspectiveName, streamId, streamsProcessed, totalStreams);
        }
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
      Message = "Starting {Mode} rebuild of perspective {Perspective} — {StreamCount} streams")]
  private static partial void LogRebuildStarting(ILogger logger, RebuildMode mode, string perspective, int streamCount);

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Completed {Mode} rebuild of perspective {Perspective} — {Streams} streams in {ElapsedMs}ms")]
  private static partial void LogRebuildCompleted(ILogger logger, RebuildMode mode, string perspective, int streams, long elapsedMs);

  [LoggerMessage(Level = LogLevel.Error,
      Message = "Failed {Mode} rebuild of perspective {Perspective} after {Streams} streams in {ElapsedMs}ms")]
  private static partial void LogRebuildFailed(ILogger logger, Exception ex, RebuildMode mode, string perspective, int streams, long elapsedMs);

  [LoggerMessage(Level = LogLevel.Warning,
      Message = "Rebuild {Perspective}: failed on stream {StreamId} ({Processed}/{Total})")]
  private static partial void LogStreamFailed(ILogger logger, Exception ex, string perspective, Guid streamId, int processed, int total);
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
