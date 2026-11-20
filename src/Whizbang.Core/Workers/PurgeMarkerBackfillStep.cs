// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Workers;

/// <summary>
/// Writes the purge markers that rows purged before markers existed never got (#2314), so the companion cleanup (#2294)
/// can remove their cursor and snapshots.
/// </summary>
/// <remarks>
/// <para>
/// A missing row is marked only when the generator proves it was purged: its cursor's last event is a type whose Apply
/// on that perspective can only purge (<see cref="PerspectiveRegistrationInfo.UnconditionalPurgeEventTypes"/>). A row can
/// also be missing because it was never created, because an older build evicted it, or because its stream still has work
/// to apply; a marker on any of those makes the runner skip the stream's later events. So a conditional purge from before
/// markers existed is never backfilled, and those rows keep their cursor and snapshots, as before.
/// </para>
/// <para>
/// Each cycle writes at most <see cref="BATCH_SIZE"/> markers. Once a cycle writes none, the backfill is done for this
/// process: the step reports, once, how many missing rows it left unmarked (up to <see cref="COUNT_CAP"/>), and asks the
/// store nothing more until the host restarts.
/// </para>
/// </remarks>
/// <param name="logger">The step's logger.</param>
/// <docs>fundamentals/perspectives/perspectives-with-actions#purge-stays-purged</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Workers/PurgeMarkerBackfillStepTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PurgeMarkerBackfillSqlTests.cs</tests>
public sealed partial class PurgeMarkerBackfillStep(ILogger<PurgeMarkerBackfillStep> logger) : IMaintenanceStep {
  /// <summary>The most purge markers one cycle writes.</summary>
  public const int BATCH_SIZE = 5000;

  /// <summary>The most missing rows left unmarked that the report counts.</summary>
  public const int COUNT_CAP = 10000;

  private readonly ILogger<PurgeMarkerBackfillStep> _logger = ArgumentGuard.NotNull(logger);
  private volatile bool _done;

  /// <inheritdoc />
  public string Name => "purge-marker-backfill";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    if (_done
        || services.GetService<IPerspectiveRunnerRegistry>() is not { } registry
        || services.GetService<IPerspectivePurgeMarkerStore>() is not { } store) {
      return;
    }
    (string PerspectiveName, string EventType)[] purging = [.. registry.GetRegisteredPerspectives()
      .SelectMany(p => p.UnconditionalPurgeEventTypes.Select(t => (p.ClrTypeName, t)))];
    if (purging.Length > 0) {
      var marked = await store.BackfillMarkersAsync(purging, BATCH_SIZE, cancellationToken).ConfigureAwait(false);
      if (marked > 0) {
        LogMarked(_logger, marked);
        return;
      }
    }
    _done = true;
    var unmarked = await store.CountUnmarkedRowlessPairsAsync(purging, COUNT_CAP, cancellationToken).ConfigureAwait(false);
    if (unmarked > 0) {
      LogLeftUnmarked(_logger, unmarked, COUNT_CAP);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information,
    Message = "Purge-marker backfill: {Marked} purge marker(s) written for rows purged before markers existed; the maintenance cycle now removes their cursors and snapshots")]
  static partial void LogMarked(ILogger logger, int marked);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information,
    Message = "Purge-marker backfill done: {Unmarked} missing row(s) (counted up to {Cap}) keep their cursor and snapshots, because their last event can leave a row in place (a conditional purge, or a row never created), so a marker could skip later events")]
  static partial void LogLeftUnmarked(ILogger logger, long unmarked, int cap);
}
