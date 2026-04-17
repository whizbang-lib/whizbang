using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Handles <see cref="RebuildPerspectiveCommand"/> by invoking <see cref="IPerspectiveRebuilder"/>
/// for each requested perspective. Without this receptor the command is defined but has no effect —
/// the rebuilder is never called, projections are not rebuilt, and <c>wh_perspective_cursors</c>
/// never updates. This is the missing turnkey piece between the system command and the rebuilder.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Fan-out:</strong> when <c>PerspectiveNames</c> is null or empty, the receptor iterates
/// every perspective in <see cref="IPerspectiveRunnerRegistry"/> and rebuilds each. Matches the
/// documented API: "Null = all registered perspectives".
/// </para>
/// <para>
/// <strong>Stream filter:</strong> when <c>IncludeStreamIds</c> is non-empty, the rebuilder's
/// <see cref="IPerspectiveRebuilder.RebuildStreamsAsync"/> runs for just those streams. When only
/// <c>ExcludeStreamIds</c> is set, the full set of streams is fetched from
/// <see cref="IEventStoreQuery"/> and the exclusions subtracted. Otherwise the mode-based method
/// (BlueGreen or InPlace) runs.
/// </para>
/// <para>
/// <strong>Unsupported fields:</strong> <c>FromEventId</c> is logged as a warning and ignored —
/// <see cref="IPerspectiveRebuilder"/> has no partial-range replay API. Adding it is tracked as
/// a follow-up.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild</docs>
public sealed partial class RebuildPerspectiveCommandReceptor(
    IServiceScopeFactory scopeFactory,
    ILogger<RebuildPerspectiveCommandReceptor> logger) : IReceptor<RebuildPerspectiveCommand> {

  /// <inheritdoc />
  public async ValueTask HandleAsync(
      RebuildPerspectiveCommand message,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);

    if (message.FromEventId.HasValue) {
      LogFromEventIdUnsupported(logger, message.FromEventId.Value);
    }

    // Resolve scoped dependencies fresh per invocation. The receptor is held as a singleton
    // in IReceptorRegistry (runtime-registered at startup), so we can't capture IEventStoreQuery
    // or IPerspectiveRunnerRegistry in the constructor — they're driver-scoped.
    await using var scope = scopeFactory.CreateAsyncScope();
    var sp = scope.ServiceProvider;
    var rebuilder = sp.GetRequiredService<IPerspectiveRebuilder>();
    var runnerRegistry = sp.GetRequiredService<IPerspectiveRunnerRegistry>();

    var perspectiveNames = _resolvePerspectiveNames(message.PerspectiveNames, runnerRegistry);
    if (perspectiveNames.Length == 0) {
      LogNoPerspectivesRegistered(logger);
      return;
    }

    var streamFilter = await _resolveStreamFilterAsync(message, sp, cancellationToken);

    foreach (var perspectiveName in perspectiveNames) {
      cancellationToken.ThrowIfCancellationRequested();

      RebuildResult result;
      if (streamFilter is not null) {
        result = await rebuilder.RebuildStreamsAsync(perspectiveName, streamFilter, cancellationToken);
      } else if (message.Mode == RebuildMode.InPlace) {
        result = await rebuilder.RebuildInPlaceAsync(perspectiveName, cancellationToken);
      } else {
        // Default (BlueGreen) and SelectedStreams-without-filter both fall through here.
        // The rebuilder's _rebuildCoreAsync accepts the mode value only for logging; behavior
        // is identical to InPlace until a real blue-green implementation lands.
        result = await rebuilder.RebuildBlueGreenAsync(perspectiveName, cancellationToken);
      }

      if (!result.Success) {
        LogRebuildFailed(logger, perspectiveName, result.Error ?? "(no error message)");
      } else {
        LogRebuildSucceeded(logger, perspectiveName, result.StreamsProcessed, result.Duration.TotalMilliseconds);
      }
    }
  }

  private static string[] _resolvePerspectiveNames(
      string[]? requested, IPerspectiveRunnerRegistry runnerRegistry) {
    // System commands broadcast to every service via SharedTopicInboxStrategy — each service
    // receives this command and independently decides which perspectives IT owns. The
    // receptor always intersects the requested set with the local registry so a service only
    // rebuilds what it actually hosts. No central ownership / dispatch is required.
    var localNames = runnerRegistry.GetRegisteredPerspectives().Select(p => p.ClrTypeName);

    if (requested is { Length: > 0 }) {
      var requestedSet = new HashSet<string>(requested);
      return [.. localNames.Where(requestedSet.Contains)];
    }

    // Null / empty → fan out to every locally registered perspective.
    return [.. localNames];
  }

  private static async Task<IReadOnlyList<Guid>?> _resolveStreamFilterAsync(
      RebuildPerspectiveCommand message, IServiceProvider scopedProvider, CancellationToken cancellationToken) {
    var include = message.IncludeStreamIds;
    var exclude = message.ExcludeStreamIds;

    if (include is { Length: > 0 }) {
      return include;
    }

    if (exclude is { Length: > 0 }) {
      var eventStoreQuery = scopedProvider.GetRequiredService<IEventStoreQuery>();
      var allStreams = new List<Guid>();
      var query = eventStoreQuery.Query.Select(e => e.StreamId).Distinct();
      if (query is IAsyncEnumerable<Guid> asyncEnumerable) {
        await foreach (var id in asyncEnumerable.WithCancellation(cancellationToken)) {
          allStreams.Add(id);
        }
      } else {
        allStreams.AddRange(query);
      }
      var excludeSet = new HashSet<Guid>(exclude);
      return [.. allStreams.Where(id => !excludeSet.Contains(id))];
    }

    return null;
  }

  [LoggerMessage(Level = LogLevel.Warning,
      Message = "RebuildPerspectiveCommand.FromEventId={FromEventId} is set but IPerspectiveRebuilder has no partial-range replay API; the value is ignored and the rebuild replays from event zero.")]
  private static partial void LogFromEventIdUnsupported(ILogger logger, long fromEventId);

  [LoggerMessage(Level = LogLevel.Warning,
      Message = "RebuildPerspectiveCommand received but no perspectives are registered — nothing to rebuild.")]
  private static partial void LogNoPerspectivesRegistered(ILogger logger);

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Rebuilt perspective {PerspectiveName}: {StreamsProcessed} streams in {ElapsedMs}ms")]
  private static partial void LogRebuildSucceeded(ILogger logger, string perspectiveName, int streamsProcessed, double elapsedMs);

  [LoggerMessage(Level = LogLevel.Error,
      Message = "Rebuild of perspective {PerspectiveName} failed: {Error}")]
  private static partial void LogRebuildFailed(ILogger logger, string perspectiveName, string error);
}
