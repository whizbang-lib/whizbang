using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// One bucket's report inputs for the batched ledger consult — the same values
/// <see cref="IIntegrityRepairLedger.TryBeginReportAsync"/> takes one key at a time.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public readonly record struct IntegrityReportObservation(
  IntegrityRepairLedger.DivergenceKey Key, long OriginLo, long OriginHi, long LocalLo, long LocalHi);

/// <summary>
/// One repair-eligible ledger row claimed by the paced drain: the bucket's identity plus the
/// compared window stamped at discovery ([WindowFrom, WindowUntil] origin commit sequences; null
/// when the row predates window stamping — dispatch then derives a coarser range per origin).
/// </summary>
/// <docs>proposals/paced-repair-drain</docs>
public readonly record struct IntegrityRepairDrainItem(
  Guid OriginServiceId, string? TenantScope, string EventType, Guid StreamId,
  long? WindowFrom, long? WindowUntil);

/// <summary>
/// Convergence state for stream integrity: what divergence has already been reported, and how
/// often a bucket's repair has been requested.
/// </summary>
/// <remarks>
/// <para>
/// The difference between implementations is not an optimisation. The in-memory
/// <see cref="IntegrityRepairLedger"/> is per-process and dies on restart, which is sound only
/// while restarts are rare and the divergent set is small. When a report storm is what CAUSES the
/// restarts, clearing this memory every boot re-reports every divergent bucket, and the flood
/// feeds the restarts that clear the memory. It is also per-replica, so N pods ask about the same
/// divergence N times — something no in-memory bounding can address.
/// </para>
/// <para>
/// A durable implementation makes the key the identity of a divergence, so "have we already asked
/// about this exact thing?" survives restarts and is shared across replicas.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
public interface IIntegrityRepairLedger {
  /// <summary>
  /// True when this divergence should be REPORTED now: first sighting, a changed signature
  /// (either side's digest moved — progress or fresh damage, which also resets the repair
  /// budget), or the cooldown elapsed since the last report. Records the sighting either way.
  /// </summary>
  ValueTask<bool> TryBeginReportAsync(
    IntegrityRepairLedger.DivergenceKey key, long originLo, long originHi, long localLo, long localHi,
    DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default);

  /// <summary>
  /// True when a repair request should be SENT now: the first attempt goes immediately, each later
  /// attempt waits base × 2^(n-1), and past <paramref name="maxAttempts"/> the requester stops
  /// asking until the bucket's signature changes. Records the attempt when true.
  /// </summary>
  ValueTask<bool> TryBeginRepairAsync(
    IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
    CancellationToken cancellationToken = default);

  /// <summary>The bucket folded identical — forget it. A later divergence is a brand-new incident.</summary>
  ValueTask MarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken cancellationToken = default);

  /// <summary>
  /// Batched <see cref="TryBeginReportAsync"/>: element i answers observation i. A manifest chunk
  /// carries hundreds of buckets, and consulting the ledger per bucket made each comparison
  /// seconds long — slower than manifests arrive, which queued arrivals (payloads and all) in
  /// memory behind the comparator's gate until the process died. Durable implementations override
  /// with one round trip; this default preserves exact semantics by looping the single.
  /// </summary>
  async ValueTask<IReadOnlyList<bool>> TryBeginReportBatchAsync(
    IReadOnlyList<IntegrityReportObservation> observations, DateTimeOffset now, TimeSpan cooldown,
    CancellationToken cancellationToken = default) {
    var results = new bool[observations.Count];
    for (var i = 0; i < observations.Count; i++) {
      var o = observations[i];
      results[i] = await TryBeginReportAsync(
        o.Key, o.OriginLo, o.OriginHi, o.LocalLo, o.LocalHi, now, cooldown, cancellationToken).ConfigureAwait(false);
    }
    return results;
  }

  /// <summary>
  /// Batched <see cref="TryBeginRepairAsync"/>, capped at <paramref name="maxGrants"/> IN ORDER.
  /// Past the cap, keys are not consulted at all — a grant records an attempt, and a grant the
  /// caller must discard burns backoff budget for nothing. Element i answers key i.
  /// </summary>
  async ValueTask<IReadOnlyList<bool>> TryBeginRepairBatchAsync(
    IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, DateTimeOffset now, TimeSpan baseBackoff,
    int maxAttempts, int maxGrants, CancellationToken cancellationToken = default) {
    var results = new bool[keys.Count];
    var granted = 0;
    for (var i = 0; i < keys.Count; i++) {
      if (granted >= maxGrants) {
        continue;   // stays false
      }
      results[i] = await TryBeginRepairAsync(keys[i], now, baseBackoff, maxAttempts, cancellationToken).ConfigureAwait(false);
      if (results[i]) {
        granted++;
      }
    }
    return results;
  }

  /// <summary>Batched <see cref="MarkHealedAsync"/> — forget a chunk's healed buckets at once.</summary>
  async ValueTask MarkHealedBatchAsync(
    IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, CancellationToken cancellationToken = default) {
    foreach (var key in keys) {
      await MarkHealedAsync(key, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// <see cref="MarkHealedBatchAsync"/>, additionally returning each healed bucket's age in
  /// seconds (first sighting → this heal — the per-bucket time-to-reconcile). The heal was
  /// already destroying the row that carried the clock; an implementation that can read the age
  /// back out of that destruction does, at zero extra cost. The default heals without ages —
  /// an empty result means "not measured", never "instant".
  /// </summary>
  async ValueTask<IReadOnlyList<double>> MarkHealedBatchWithAgesAsync(
    IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, CancellationToken cancellationToken = default) {
    await MarkHealedBatchAsync(keys, cancellationToken).ConfigureAwait(false);
    return [];
  }
}
