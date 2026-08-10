using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Durable stream-integrity ledger, backed by <c>wh_integrity_ledger</c> through the coordinator.
/// </summary>
/// <remarks>
/// Replaces per-process memory with a row per divergent bucket, which changes two things memory
/// could not. The cooldown and repair-attempt count survive a restart, so a report storm can no
/// longer clear the very state that would suppress it — the loop where saturation causes the
/// restarts that erase the memory that would have prevented the saturation. And the row is shared,
/// so concurrent replicas cannot each report the same divergence: the backing functions take a row
/// lock and exactly one caller is told to proceed.
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityLedgerSqlTests.cs</tests>
public sealed class CoordinatorIntegrityRepairLedger(IServiceScopeFactory scopeFactory) : IIntegrityRepairLedger {

  /// <inheritdoc />
  public async ValueTask<bool> TryBeginReportAsync(
      IntegrityRepairLedger.DivergenceKey key, long originLo, long originHi, long localLo, long localHi,
      DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) {
    using var scope = scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
    if (coordinator is null) {
      return true;   // no store — behave as before this existed
    }
    return await coordinator.IntegrityTryBeginReportAsync(
      key, originLo, originHi, localLo, localHi, now, cooldown, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask<bool> TryBeginRepairAsync(
      IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
      CancellationToken cancellationToken = default) {
    using var scope = scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
    if (coordinator is null) {
      return false;  // never license an unbounded repair against real data
    }
    return await coordinator.IntegrityTryBeginRepairAsync(
      key, now, baseBackoff, maxAttempts, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async ValueTask MarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken cancellationToken = default) {
    using var scope = scopeFactory.CreateScope();
    var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
    if (coordinator is not null) {
      await coordinator.IntegrityMarkHealedAsync(key, cancellationToken).ConfigureAwait(false);
    }
  }
}
