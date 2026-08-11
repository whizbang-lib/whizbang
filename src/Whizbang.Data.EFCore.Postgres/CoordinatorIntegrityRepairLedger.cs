using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
public sealed class CoordinatorIntegrityRepairLedger(
    IServiceScopeFactory scopeFactory,
    ILogger<CoordinatorIntegrityRepairLedger>? logger = null) : IIntegrityRepairLedger {

  private readonly ILogger _log =
    logger ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger<CoordinatorIntegrityRepairLedger>.Instance;

  private void _degraded(string operation, bool fallback, Exception ex) =>
#pragma warning disable CA1848 // Rare error path; a source-generated message would need a partial type.
    _log.LogWarning(ex,
      "Integrity ledger {Operation} failed; falling back to {Fallback}. Convergence bounding is " +
      "degraded until this is resolved.", operation, fallback);
#pragma warning restore CA1848


  /// <inheritdoc />
  public async ValueTask<bool> TryBeginReportAsync(
      IntegrityRepairLedger.DivergenceKey key, long originLo, long originHi, long localLo, long localHi,
      DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) {
    try {
      using var scope = scopeFactory.CreateScope();
      var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
      if (coordinator is null) {
        return true;   // no store — behave as before this existed
      }
      return await coordinator.IntegrityTryBeginReportAsync(
        key, originLo, originHi, localLo, localHi, now, cooldown, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      // Defence in depth. The Postgres coordinator catches internally, but this seam must hold for
      // ANY implementation and for a DI resolution failure: convergence bookkeeping breaking must
      // never take down the audit that finds real data loss. Logged, never silent — a degraded
      // ledger that looks healthy is the failure this whole change exists to prevent.
      _degraded("report", fallback: true, ex);
      return true;
    }
  }

  /// <inheritdoc />
  public async ValueTask<bool> TryBeginRepairAsync(
      IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
      CancellationToken cancellationToken = default) {
    try {
      using var scope = scopeFactory.CreateScope();
      var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
      if (coordinator is null) {
        return false;  // never license an unbounded repair against real data
      }
      return await coordinator.IntegrityTryBeginRepairAsync(
        key, now, baseBackoff, maxAttempts, cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      _degraded("repair", fallback: false, ex);   // the opposite default, for the opposite reason
      return false;
    }
  }

  /// <inheritdoc />
  /// <remarks>One round trip per chunk (the per-bucket consult made comparisons slower than
  /// manifests arrive, which queued arrivals in memory until the process died). A failed or
  /// unsupported batch falls back to the single-key path, whose per-operation fail-open /
  /// fail-closed semantics remain the authority.</remarks>
  public async ValueTask<System.Collections.Generic.IReadOnlyList<bool>> TryBeginReportBatchAsync(
      System.Collections.Generic.IReadOnlyList<IntegrityReportObservation> observations,
      DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) {
    if (observations.Count > 0) {
      try {
        using var scope = scopeFactory.CreateScope();
        var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
        var batch = coordinator is null
          ? null
          : await coordinator.IntegrityTryBeginReportBatchAsync(
              observations[0].Key.OriginServiceId, observations, now, cooldown, cancellationToken).ConfigureAwait(false);
        if (batch is not null && batch.Count == observations.Count) {
          return batch;
        }
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        _degraded("report-batch", fallback: true, ex);
      }
    }
    var results = new bool[observations.Count];
    for (var i = 0; i < observations.Count; i++) {
      var o = observations[i];
      results[i] = await TryBeginReportAsync(
        o.Key, o.OriginLo, o.OriginHi, o.LocalLo, o.LocalHi, now, cooldown, cancellationToken).ConfigureAwait(false);
    }
    return results;
  }

  /// <inheritdoc />
  public async ValueTask<System.Collections.Generic.IReadOnlyList<bool>> TryBeginRepairBatchAsync(
      System.Collections.Generic.IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
      DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts, int maxGrants,
      CancellationToken cancellationToken = default) {
    if (keys.Count > 0 && maxGrants > 0) {
      try {
        using var scope = scopeFactory.CreateScope();
        var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
        var batch = coordinator is null
          ? null
          : await coordinator.IntegrityTryBeginRepairBatchAsync(
              keys[0].OriginServiceId, keys, now, baseBackoff, maxAttempts, maxGrants, cancellationToken).ConfigureAwait(false);
        if (batch is not null && batch.Count == keys.Count) {
          return batch;
        }
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        _degraded("repair-batch", fallback: false, ex);
      }
    }
    var results = new bool[keys.Count];
    var granted = 0;
    for (var i = 0; i < keys.Count; i++) {
      if (granted >= maxGrants) {
        continue;
      }
      results[i] = await TryBeginRepairAsync(keys[i], now, baseBackoff, maxAttempts, cancellationToken).ConfigureAwait(false);
      if (results[i]) {
        granted++;
      }
    }
    return results;
  }

  /// <inheritdoc />
  public async ValueTask MarkHealedBatchAsync(
      System.Collections.Generic.IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys,
      CancellationToken cancellationToken = default) {
    if (keys.Count == 0) {
      return;
    }
    try {
      using var scope = scopeFactory.CreateScope();
      var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
      if (coordinator is not null
          && await coordinator.IntegrityMarkHealedBatchAsync(keys[0].OriginServiceId, keys, cancellationToken).ConfigureAwait(false)) {
        return;
      }
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      _degraded("mark-healed-batch", fallback: false, ex);
    }
    foreach (var key in keys) {
      await MarkHealedAsync(key, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  public async ValueTask MarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken cancellationToken = default) {
    try {
      using var scope = scopeFactory.CreateScope();
      var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
      if (coordinator is not null) {
        await coordinator.IntegrityMarkHealedAsync(key, cancellationToken).ConfigureAwait(false);
      }
    } catch (OperationCanceledException) {
      throw;
    } catch (Exception ex) {
      // Nothing to fall back to — a forgotten heal just means the bucket is re-offered later,
      // which is the harmless direction.
      _degraded("mark-healed", fallback: false, ex);
    }
  }
}
