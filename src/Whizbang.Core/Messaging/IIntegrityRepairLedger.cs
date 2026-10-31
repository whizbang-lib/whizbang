// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// One bucket's report inputs for the batched ledger consult — the same values
/// <see cref="IIntegrityRepairLedger.TryBeginReportAsync"/> takes one key at a time, plus the kind of
/// divergence the comparison found.
/// </summary>
/// <param name="Key">The divergent bucket.</param>
/// <param name="OriginLo">Low half of the origin's fold digest.</param>
/// <param name="OriginHi">High half of the origin's fold digest.</param>
/// <param name="LocalLo">Low half of the local fold digest (zero when the bucket is absent locally).</param>
/// <param name="LocalHi">High half of the local fold digest (zero when the bucket is absent locally).</param>
/// <param name="IsDeficit">True when the consumer holds fewer events than the origin, the only divergence a
/// redelivery can repair. False for an identity mismatch (equal counts, different folds) and a local surplus:
/// those are recorded for reporting and are never claimed by the paced repair drain. The kind cannot be
/// derived from the four digest halves, which are hashes rather than counts, so the comparison states it.</param>
/// <docs>resilience/stream-integrity#drain-claims-deficits-only</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PacedRepairDrainSqlTests.cs</tests>
public readonly record struct IntegrityReportObservation(
  IntegrityRepairLedger.DivergenceKey Key, long OriginLo, long OriginHi, long LocalLo, long LocalHi, bool IsDeficit);

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
/// What a manifest comparison needs to know before it adds deficit buckets to the paced repair
/// drain's ledger: how large the drain's backlog already is, and which of the comparison's keys the
/// ledger already tracks (re-admitting a tracked key adds nothing to the backlog).
/// </summary>
/// <param name="Backlog">Per-stream ledger rows the drain may claim (deficits not marked unhealable), across every origin,
/// counted up to the cap the caller passed (a count at the cap means "at least the cap").</param>
/// <param name="Tracked">Element i is true when key i already has a deficit row in the ledger. A row
/// recorded as a mismatch, a surplus or of unknown kind is not a queued repair, so it does not count.</param>
/// <docs>resilience/stream-integrity#repair-caps</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityRepairBacklogProbeSqlTests.cs</tests>
public sealed record IntegrityRepairBacklogProbe(long Backlog, IReadOnlyList<bool> Tracked);

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
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "A divergence is identified by both sides' commit-sequence bounds, so the four longs are one comparison rather than four settings. The clock and the cooldown are passed in so the ledger holds no ambient time.")]
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

  /// <summary>
  /// Records that repair cannot change these buckets: a redelivery answered for them and every event it carried
  /// was already stored under its origin, so asking again would ship the same events again. A bucket marked here
  /// is no longer granted repair (neither the burst grant nor the paced drain) and no longer re-reported on the
  /// cooldown, until its signature changes or it heals. Only buckets the ledger already tracks are marked.
  /// Returns the keys this call marked, so each is reported once; a key already marked is not returned again.
  /// The default remembers nothing and returns none, which leaves repair on its terminal cadence.
  /// </summary>
  /// <param name="keys">The buckets the redelivery covered.</param>
  /// <param name="now">The caller's clock.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The keys newly marked by this call.</returns>
  /// <docs>resilience/stream-integrity#unhealable-buckets</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityRepairLedgerUnhealableTests.cs</tests>
  ValueTask<IReadOnlyList<IntegrityRepairLedger.DivergenceKey>> MarkUnhealableBatchAsync(
    IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, DateTimeOffset now,
    CancellationToken cancellationToken = default) =>
    ValueTask.FromResult<IReadOnlyList<IntegrityRepairLedger.DivergenceKey>>([]);
}
