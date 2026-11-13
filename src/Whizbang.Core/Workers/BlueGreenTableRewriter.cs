// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Workers;

/// <summary>
/// The <see cref="TableRewriteStrategies.BLUE_GREEN"/> strategy: rewrites a perspective table by a blue-green rebuild of
/// the one perspective that writes it (#2301), the last of the online strategies <see cref="TableRewriteStrategy.Online"/>
/// tries, used only when neither pg_squeeze nor pg_repack is usable.
/// </summary>
/// <remarks>
/// <para>
/// The rebuild replays every stream into a shadow table that took the live table's storage settings first (#2299), so
/// the rebuilt table is written at the declared fillfactor, and swaps it in under a bounded lock: the outcome a rewrite
/// exists for, with no extension and no long lock. A replay costs more I/O than copying rows, which is why it ranks last.
/// </para>
/// <para>
/// Only a table the perspective registry records exactly one perspective as writing can be rebuilt
/// (<see cref="IPerspectiveTableSwapper.FindPerspectiveAsync"/>); a framework table, or one several perspectives share,
/// cannot be recreated from one perspective's events and waits for an extension. A table another blue-green rebuild
/// holds, on this instance or any other, is not rebuilt again: <see cref="BusyReasonAsync"/> says so and the request
/// stays queued, and the rebuilder's own guard refuses a second rebuild that starts anyway. After the swap the
/// coordinator re-measures the table, as it does for the other strategies.
/// </para>
/// </remarks>
/// <param name="scopeFactory">The scope the swapper and the rebuilder are resolved from.</param>
/// <param name="logger">Where a rebuild that did not complete is reported.</param>
/// <docs>operations/infrastructure/storage-maintenance#blue-green-rewrite</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/OnlineTableRewriteStepTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/BlueGreenRebuildIntegrationTests.cs</tests>
public sealed partial class BlueGreenTableRewriter(IServiceScopeFactory scopeFactory, ILogger<BlueGreenTableRewriter> logger) : ITableRewriter {
  private readonly IServiceScopeFactory _scopeFactory = ArgumentGuard.NotNull(scopeFactory);
  private readonly ILogger<BlueGreenTableRewriter> _logger = ArgumentGuard.NotNull(logger);

  /// <inheritdoc />
  public string Strategy => TableRewriteStrategies.BLUE_GREEN;

  /// <inheritdoc />
  public bool CanRun => true;

  /// <inheritdoc />
  public string CannotRunReason => "none: a blue-green rebuild needs nothing beyond Whizbang";

  /// <summary>
  /// The one perspective that writes <paramref name="table"/>, or null when no single rebuild can recreate it: it is not
  /// a perspective table, several perspectives write it, or the host has no swapper or no rebuilder.
  /// </summary>
  /// <param name="table">The bare table name.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public async Task<string?> PerspectiveOfAsync(string table, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(table);
    await using var scope = _scopeFactory.CreateAsyncScope();
    var swapper = scope.ServiceProvider.GetService<IPerspectiveTableSwapper>();
    if (swapper is null || scope.ServiceProvider.GetService<IPerspectiveRebuilder>() is null) {
      return null;
    }
    return await swapper.FindPerspectiveAsync(table, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Why the table cannot be rebuilt now because a rebuild of it is already running (another instance holds its guard, or
  /// this instance is rebuilding the perspective), or null when it can. A rebuild in flight satisfies the request for now:
  /// it stays queued, and a later cycle sees whether it is still owed.
  /// </summary>
  /// <param name="table">The bare table name.</param>
  /// <param name="perspective">The perspective that writes it.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public async Task<string?> BusyReasonAsync(string table, string perspective, CancellationToken cancellationToken) {
    await using var scope = _scopeFactory.CreateAsyncScope();
    var rebuilder = scope.ServiceProvider.GetRequiredService<IPerspectiveRebuilder>();
    if (await rebuilder.GetRebuildStatusAsync(perspective, cancellationToken).ConfigureAwait(false) is not null) {
      return $"a rebuild of {perspective} is in flight on this instance";
    }
    var swapper = scope.ServiceProvider.GetRequiredService<IPerspectiveTableSwapper>();
    var guard = await swapper.TryLockRebuildAsync(table, cancellationToken).ConfigureAwait(false);
    if (guard is null) {
      return $"another blue-green rebuild of {table} is in flight";
    }
    await guard.DisposeAsync().ConfigureAwait(false);
    return null;
  }

  /// <inheritdoc />
  /// <remarks>
  /// Rebuilds the table's perspective blue-green, then has the coordinator measure the table as the other strategies'
  /// re-measure does. Null, with a warning, when the table has no single perspective or the rebuild did not complete.
  /// </remarks>
  public async Task<double?> RewriteAsync(IWorkCoordinator coordinator, string tableName, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(coordinator);
    if (await PerspectiveOfAsync(tableName, cancellationToken).ConfigureAwait(false) is not { } perspective) {
      LogNotRebuilt(_logger, tableName, "no single perspective writes it, so no rebuild can recreate it");
      return null;
    }
    await using var scope = _scopeFactory.CreateAsyncScope();
    var rebuilder = scope.ServiceProvider.GetRequiredService<IPerspectiveRebuilder>();
    var result = await rebuilder.RebuildBlueGreenAsync(
      perspective, new RebuildOrigin(TrackedGuid.New(), RebuildTrigger.TableRewrite), cancellationToken).ConfigureAwait(false);
    if (!result.Success) {
      LogNotRebuilt(_logger, tableName, $"the blue-green rebuild of {perspective} did not complete: {result.Error}");
      return null;
    }
    return await coordinator.MeasureTableBloatAsync(tableName, BloatMeasurementMethod.Estimate, cancellationToken).ConfigureAwait(false);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Blue-green rewrite of {Table}: {Reason}; the request stays queued")]
  private static partial void LogNotRebuilt(ILogger logger, string table, string reason);
}
