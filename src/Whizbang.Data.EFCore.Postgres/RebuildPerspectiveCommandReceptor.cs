// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.MultiStream;
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
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRebuilderIntegrationTests.cs:RebuildPerspectiveCommand_WithNamedPerspective_UpdatesCursorAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRebuilderIntegrationTests.cs:RebuildPerspectiveCommand_WithIncludeStreamIds_UpdatesOnlyTargetedCursorsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/PerspectiveRebuilderIntegrationTests.cs:RebuildPerspectiveCommand_WithNullPerspectiveNames_FansOutToAllRegisteredAsync</tests>
public sealed partial class RebuildPerspectiveCommandReceptor(
    IServiceScopeFactory scopeFactory,
    ILogger<RebuildPerspectiveCommandReceptor> logger) : IReceptor<RebuildPerspectiveCommand> {

  /// <inheritdoc />
  public async ValueTask HandleAsync(
      RebuildPerspectiveCommand message,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);

    _logCommandReceivedIfEnabled(message);

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

    var registeredNames = runnerRegistry.GetRegisteredPerspectives().Select(p => p.ClrTypeName).ToArray();
    var perspectiveNames = _resolvePerspectiveNames(message.PerspectiveNames, registeredNames);

    _logPerspectiveSelectionDiagnostics(message, perspectiveNames, registeredNames);

    if (perspectiveNames.Length == 0) {
      return;
    }

    var streamFilter = await _resolveStreamFilterAsync(message, sp, cancellationToken);
    if (streamFilter is not null) {
      LogStreamFilterApplied(logger, streamFilter.Count,
          message.IncludeStreamIds is { Length: > 0 } ? "Include" : "Exclude");
    }

    // "Selected streams, none named" is refused, never widened. It is almost always a caller that
    // meant to name streams and produced an empty list, and widening it would turn the most
    // targeted request the command offers into the largest thing it can do: a full replay, and a
    // table swap where a swapper is configured. A whole-perspective rebuild is asked for by its
    // own mode. Refused once for the request rather than once per perspective, because the mode
    // and the filter belong to the command, not to any one perspective (#1356).
    if (message.Mode == RebuildMode.SelectedStreams && streamFilter is null) {
      LogSelectedStreamsWithoutStreamIds(logger, perspectiveNames.Length);
      return;
    }

    // The caller's id when it supplied one, so it can find its own rebuild across every service that answered.
    // Otherwise one per service, which still answers "did anything run here" but cannot be correlated across them.
    var origin = new RebuildOrigin(
        message.RequestId ?? (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New(), RebuildTrigger.Requested, message.RequestedBy);

    // #1357/#1353: where the work runs. With a multi-stream store, a rebuild of named streams, or an in-place rebuild, is
    // recorded as a run and each stream is rebuilt by the instance that owns it, under that stream's apply lock there; this
    // instance rebuilds nothing itself, so it neither races the owners' live work nor holds the handler for the rebuild.
    // A blue-green rebuild builds and swaps one table, so it stays one job, handed to the background queue so the
    // handler returns at once. Without a store or a queue, the rebuild runs here, as it always has.
    var store = sp.GetService<IMultiStreamCommandStore>();
    var routeToOwners = store is { IsConfigured: true };
    var queue = sp.GetService<RebuildBackgroundQueue>();

    var routing = new RebuildRouting(routeToOwners ? store : null, queue, sp, origin, message.RequestId);
    foreach (var perspectiveName in perspectiveNames) {
      cancellationToken.ThrowIfCancellationRequested();
      await _rebuildPerspectiveAsync(rebuilder, perspectiveName, message.Mode, streamFilter, routing, cancellationToken);
    }
  }

  /// <summary>Where one perspective's rebuild runs: a run on the streams' owners, the background queue, or here.</summary>
  private async Task _rebuildPerspectiveAsync(
      IPerspectiveRebuilder rebuilder, string perspectiveName, RebuildMode mode, IReadOnlyList<Guid>? streamFilter,
      RebuildRouting routing, CancellationToken cancellationToken) {
    if (routing.Store is not null && (streamFilter is not null || mode == RebuildMode.InPlace)) {
      var streams = streamFilter ?? await rebuilder.ResolveStreamIdsAsync(perspectiveName, cancellationToken);
      if (streams is not null) {
        await _recordRunAsync(routing.Store, routing.Services, perspectiveName, streams, routing.Origin, routing.RequestId, cancellationToken);
        return;
      }
    }
    if (streamFilter is null && mode == RebuildMode.BlueGreen && routing.Queue is not null) {
      routing.Queue.Enqueue(perspectiveName, routing.Origin);
      LogRebuildQueued(logger, perspectiveName);
      return;
    }
    LogPerspectiveRebuildStarting(logger, perspectiveName, mode, streamFilter?.Count);
    var result = await _dispatchRebuildAsync(rebuilder, perspectiveName, mode, streamFilter, routing.Origin, cancellationToken);
    _logRebuildResult(perspectiveName, result);
  }

  /// <summary>What a rebuild can be routed to: the multi-stream store when it is configured, and the background queue.</summary>
  private sealed record RebuildRouting(
    IMultiStreamCommandStore? Store, RebuildBackgroundQueue? Queue, IServiceProvider Services, RebuildOrigin Origin, Guid? RequestId);

  private async Task _recordRunAsync(
      IMultiStreamCommandStore store, IServiceProvider sp, string perspectiveName, IReadOnlyList<Guid> streams,
      RebuildOrigin origin, Guid? requestId, CancellationToken cancellationToken) {
    if (streams.Count == 0) {
      // Nothing to rebuild is nothing to do: an empty list is never read as every stream (#1356).
      LogNothingToRoute(logger, perspectiveName);
      return;
    }
    var runId = requestId is { } request ? _runIdFor(request, perspectiveName) : (Guid)Whizbang.Core.ValueObjects.TrackedGuid.New();
    var intake = new MultiStreamCommandIntake<RebuildPerspectiveStreamsCommand>(
      store,
      sp.GetService<ILogger<MultiStreamCommandIntake<RebuildPerspectiveStreamsCommand>>>()
        ?? NullLogger<MultiStreamCommandIntake<RebuildPerspectiveStreamsCommand>>.Instance);
    await intake.HandleAsync(new RebuildPerspectiveStreamsCommand(runId, streams, perspectiveName, origin), cancellationToken);
    LogRebuildRouted(logger, perspectiveName, streams.Count, runId);
  }

  /// <summary>
  /// One run per (request, perspective), the same however often the command is delivered: a redelivered rebuild command
  /// finds its run already recorded instead of rebuilding every stream again.
  /// </summary>
  private static Guid _runIdFor(Guid requestId, string perspectiveName) {
    var name = System.Text.Encoding.UTF8.GetBytes(perspectiveName);
    var input = new byte[16 + name.Length];
    requestId.TryWriteBytes(input);
    name.CopyTo(input, 16);
    return new Guid(System.Security.Cryptography.SHA256.HashData(input).AsSpan(0, 16));
  }

  /// <summary>IsEnabled-guarded "command received" log. Separated so the receptor's HandleAsync
  /// stays linear; the CA1873 pragma stays scoped to where string.Join actually runs.</summary>
  private void _logCommandReceivedIfEnabled(RebuildPerspectiveCommand message) {
#pragma warning disable CA1873 // string.Join is guarded by IsEnabled check.
    if (logger.IsEnabled(LogLevel.Information)) {
      LogCommandReceived(
          logger,
          message.Mode,
          message.PerspectiveNames is null ? "<all-registered>" : string.Join(",", message.PerspectiveNames),
          message.IncludeStreamIds?.Length ?? 0,
          message.ExcludeStreamIds?.Length ?? 0,
          message.FromEventId);
    }
#pragma warning restore CA1873
  }

  /// <summary>Emits the "skipped not owned", "selected", and "nothing to rebuild" logs. Keeps
  /// the CA1873 pragma local and lets HandleAsync return cleanly when the selection is empty.</summary>
  private void _logPerspectiveSelectionDiagnostics(
      RebuildPerspectiveCommand message, string[] perspectiveNames, string[] registeredNames) {
#pragma warning disable CA1873 // string.Join calls are guarded by explicit IsEnabled checks above each LogXxx call.
    if (message.PerspectiveNames is { Length: > 0 } && logger.IsEnabled(LogLevel.Information)) {
      var skipped = message.PerspectiveNames.Except(perspectiveNames).ToArray();
      if (skipped.Length > 0) {
        LogPerspectivesSkippedNotOwned(logger, string.Join(",", skipped),
            string.Join(",", registeredNames));
      }
    }

    if (perspectiveNames.Length == 0) {
      if (logger.IsEnabled(LogLevel.Warning)) {
        LogNoPerspectivesToRebuild(logger, message.PerspectiveNames is null,
            string.Join(",", registeredNames));
      }
      return;
    }

    if (logger.IsEnabled(LogLevel.Information)) {
      LogPerspectivesSelected(logger, perspectiveNames.Length, string.Join(",", perspectiveNames));
    }
#pragma warning restore CA1873
  }

  /// <summary>Selects the rebuild method based on filter presence and mode. When
  /// <paramref name="streamFilter"/> is non-null the stream-filtered overload runs; otherwise
  /// the mode picks InPlace vs BlueGreen.</summary>
  /// <remarks>
  /// <paramref name="mode"/> is never <see cref="RebuildMode.SelectedStreams"/> with a null
  /// <paramref name="streamFilter"/>: the caller refuses that combination before dispatching, so
  /// it cannot reach the BlueGreen fall-through below. It used to, and a selected-streams request
  /// whose stream list came out empty rebuilt every stream of the perspective instead (#1356).
  /// </remarks>
  private static Task<RebuildResult> _dispatchRebuildAsync(
      IPerspectiveRebuilder rebuilder,
      string perspectiveName,
      RebuildMode mode,
      IReadOnlyList<Guid>? streamFilter,
      RebuildOrigin origin,
      CancellationToken cancellationToken) {
    if (streamFilter is not null) {
      return rebuilder.RebuildStreamsAsync(perspectiveName, streamFilter, origin, cancellationToken);
    }
    if (mode == RebuildMode.InPlace) {
      return rebuilder.RebuildInPlaceAsync(perspectiveName, origin, cancellationToken);
    }
    return rebuilder.RebuildBlueGreenAsync(perspectiveName, origin, cancellationToken);
  }

  /// <summary>Paired success/failure log from the rebuild loop body.</summary>
  private void _logRebuildResult(string perspectiveName, RebuildResult result) {
    if (!result.Success) {
      LogRebuildFailed(logger, perspectiveName, result.Error ?? "(no error message)");
    } else {
      LogRebuildSucceeded(logger, perspectiveName, result.StreamsProcessed, result.Duration.TotalMilliseconds);
    }
  }

  private static string[] _resolvePerspectiveNames(
      string[]? requested, string[] registeredNames) {
    // System commands broadcast to every service via SharedTopicInboxStrategy — each service
    // receives this command and independently decides which perspectives IT owns. The
    // receptor always intersects the requested set with the local registry so a service only
    // rebuilds what it actually hosts. No central ownership / dispatch is required.
    if (requested is { Length: > 0 }) {
      var requestedSet = new HashSet<string>(requested);
      return [.. registeredNames.Where(requestedSet.Contains)];
    }

    // Null / empty → fan out to every locally registered perspective.
    return registeredNames;
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

  [LoggerMessage(Level = LogLevel.Information,
      Message = "RebuildPerspectiveCommand received: Mode={Mode}, PerspectiveNames=[{RequestedNames}], IncludeStreamIds count={IncludeCount}, ExcludeStreamIds count={ExcludeCount}, FromEventId={FromEventId}")]
  private static partial void LogCommandReceived(ILogger logger, RebuildMode mode,
      string requestedNames, int includeCount, int excludeCount, long? fromEventId);

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Perspectives skipped (not locally owned by this service): [{Skipped}]. Locally registered: [{Registered}]")]
  private static partial void LogPerspectivesSkippedNotOwned(ILogger logger, string skipped, string registered);

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Selected {Count} perspective(s) to rebuild: [{Names}]")]
  private static partial void LogPerspectivesSelected(ILogger logger, int count, string names);

  [LoggerMessage(Level = LogLevel.Warning,
      Message = "RebuildPerspectiveCommand has nothing to rebuild on this service (fanout={FanOut}). Locally registered perspectives: [{Registered}]")]
  private static partial void LogNoPerspectivesToRebuild(ILogger logger, bool fanOut, string registered);

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Applying stream filter: {Count} stream(s) ({Source})")]
  private static partial void LogStreamFilterApplied(ILogger logger, int count, string source);

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Starting rebuild of perspective {PerspectiveName} (Mode={Mode}, StreamFilterCount={StreamFilterCount})")]
  private static partial void LogPerspectiveRebuildStarting(ILogger logger, string perspectiveName,
      RebuildMode mode, int? streamFilterCount);

  [LoggerMessage(Level = LogLevel.Warning,
      Message = "RebuildPerspectiveCommand.FromEventId={FromEventId} is set but IPerspectiveRebuilder has no partial-range replay API; the value is ignored and the rebuild replays from event zero.")]
  private static partial void LogFromEventIdUnsupported(ILogger logger, long fromEventId);

  [LoggerMessage(Level = LogLevel.Warning,
      Message = "RebuildPerspectiveCommand refused: Mode=SelectedStreams names no streams (IncludeStreamIds is empty or absent, and no ExcludeStreamIds narrowed the set), so there is nothing to rebuild. {PerspectiveCount} locally owned perspective(s) were left untouched. This is not widened to a full rebuild; ask for one with Mode=BlueGreen or Mode=InPlace.")]
  private static partial void LogSelectedStreamsWithoutStreamIds(ILogger logger, int perspectiveCount);

  [LoggerMessage(Level = LogLevel.Information,
      Message = "Rebuilt perspective {PerspectiveName}: {StreamsProcessed} streams in {ElapsedMs}ms")]
  private static partial void LogRebuildSucceeded(ILogger logger, string perspectiveName, int streamsProcessed, double elapsedMs);

  [LoggerMessage(Level = LogLevel.Error,
      Message = "Rebuild of perspective {PerspectiveName} failed: {Error}")]
  private static partial void LogRebuildFailed(ILogger logger, string perspectiveName, string error);

  [LoggerMessage(EventId = 20, Level = LogLevel.Information,
    Message = "Rebuild of {PerspectiveName} recorded as multi-stream run {RunId} over {StreamCount} stream(s); each stream is rebuilt by its owner.")]
  static partial void LogRebuildRouted(ILogger logger, string perspectiveName, int streamCount, Guid runId);

  [LoggerMessage(EventId = 21, Level = LogLevel.Information,
    Message = "Blue-green rebuild of {PerspectiveName} queued to the background rebuild worker.")]
  static partial void LogRebuildQueued(ILogger logger, string perspectiveName);

  [LoggerMessage(EventId = 22, Level = LogLevel.Warning,
    Message = "Rebuild of {PerspectiveName} names no streams; nothing is recorded (an empty list is never every stream).")]
  static partial void LogNothingToRoute(ILogger logger, string perspectiveName);
}
