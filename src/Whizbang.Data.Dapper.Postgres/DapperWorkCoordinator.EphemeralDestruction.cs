// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// The ephemeral reap's destruction seam on the Dapper driver: the bodies a registered <see cref="IDestructionHook"/>
/// is offered, the hold its Cancel or Defer records, the failure count a throwing hook records, and the reap that
/// deletes only what the hook was offered.
/// </summary>
/// <remarks>
/// This coordinator used to inherit the interface's defaults for all four, so on this driver a hook was offered nothing
/// while the maintenance cycle reaped every eligible body (#2039). Each member now runs the statement the EF Core
/// coordinator runs, from <see cref="EphemeralDestructionSql"/>, qualified with this coordinator's schema. An empty
/// batch returns before a connection is opened.
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events#destruction-hooks-on-every-data-layer</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperEphemeralDestructionHookTests.cs</tests>
public partial class DapperWorkCoordinator {
  /// <inheritdoc />
  public Task<IReadOnlyList<EphemeralDestructionTarget>> GetEphemeralBodiesAboutToReapAsync(CancellationToken cancellationToken = default) =>
    _withRetentionCommandAsync((cmd, prefix) => EphemeralDestructionSql.CollectAboutToReapAsync(cmd, prefix, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task HoldEphemeralDestructionAsync(
      IReadOnlyList<Guid> eventIds, DateTimeOffset holdUntil, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventIds);
    if (eventIds.Count == 0) {
      return Task.CompletedTask;
    }
    return _withRetentionCommandAsync(async (cmd, prefix) => {
      await EphemeralDestructionSql.HoldAsync(cmd, prefix, eventIds, holdUntil, cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<int> RecordDestructionFailureAsync(
      IReadOnlyList<Guid> eventIds, DateTimeOffset retryHoldUntil, int maxRetries,
      OnDestroyFailure onFailure = OnDestroyFailure.RetryThenForcedDelete, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventIds);
    if (eventIds.Count == 0) {
      return Task.FromResult(0);
    }
    return _withRetentionCommandAsync((cmd, prefix) =>
      EphemeralDestructionSql.RecordFailureAsync(cmd, prefix, eventIds, retryHoldUntil, maxRetries, onFailure, cancellationToken),
      cancellationToken);
  }

  /// <inheritdoc />
  /// <remarks>Calls <c>wh_perform_maintenance</c> (migration 310) with the ids, in the one statement the unrestricted call runs.</remarks>
  public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(
      IReadOnlyCollection<Guid>? ephemeralReapEventIds, CancellationToken cancellationToken = default) =>
    _withRetentionCommandAsync((cmd, prefix) =>
      EphemeralDestructionSql.PerformMaintenanceAsync(cmd, prefix, ephemeralReapEventIds, cancellationToken), cancellationToken);
}
