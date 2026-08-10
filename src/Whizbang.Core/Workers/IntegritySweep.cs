using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Workers;

/// <summary>
/// #80-D: runs one full integrity SWEEP — the trust-but-verify pass (digest-table heal + full
/// unwindowed manifest exchange) that catches exactly the state the epoch seals and audit seals
/// assume is fine. Implemented by <see cref="IntegrityAuditWorker"/>; called by the scheduled
/// occurrence's receptor at the configured idle-time cron.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/IntegrityAuditWorkerTests.cs</tests>
public interface IIntegritySweepRunner {
  /// <summary>Runs one full sweep cycle now, regardless of the counter cadence.</summary>
  Task RunSweepOnceAsync(CancellationToken cancellationToken);
}

/// <summary>
/// #80-D: whether the idle-time cron owns the sweep. Set by the driver's sweep scheduler after it
/// successfully registers the schedule on the temporal engine; while true, the audit worker's
/// every-Nth-cycle counter stands down — otherwise the full-store recompute would run on BOTH
/// cadences, and the counter lands it at arbitrary load times, which is exactly what the
/// idle-time cron exists to end. Hosts without the temporal engine never set this, keeping the
/// counter as the fallback.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public sealed class IntegritySweepScheduleState {
  /// <summary>True once the sweep schedule is registered on the temporal engine.</summary>
  public bool CronActive { get; set; }
}
