using System;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

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
}
