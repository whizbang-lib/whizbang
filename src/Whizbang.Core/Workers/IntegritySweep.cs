// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/IntegrityAuditWorkerTests.cs</tests>
public interface IIntegritySweepRunner {
  /// <summary>
  /// Runs every enabled opt-in check now, regardless of the counter cadence: the full-history compare and the
  /// sealed-history re-check, each only when its section enables it (#2353). With both off it runs nothing. A schedule
  /// occurrence that names no check (one registered before #2353) runs the full-history compare only.
  /// </summary>
  Task RunSweepOnceAsync(CancellationToken cancellationToken);

  /// <summary>
  /// Runs one opt-in check now, when its section enables it, and nothing otherwise (#2353). The full-history compare
  /// runs as an audit cycle that heals the whole digest table and asks each origin for its full history; the
  /// sealed-history re-check runs on its own and asks nobody.
  /// </summary>
  /// <param name="check">The check to run.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>resilience/stream-integrity#full-history-compare</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/IntegrityAuditWorkerCheckGatingTests.cs</tests>
  Task RunCheckOnceAsync(IntegrityCheck check, CancellationToken cancellationToken);
}

/// <summary>The opt-in integrity checks that grow with history, each with its own section and schedule (#2353).</summary>
/// <docs>resilience/stream-integrity#full-history-compare</docs>
public enum IntegrityCheck {
  /// <summary>The full-history compare (<c>Whizbang:StreamIntegrity:FullHistoryCompare</c>).</summary>
  FullHistoryCompare = 0,

  /// <summary>The sealed-history re-check (<c>Whizbang:StreamIntegrity:SealRecheck</c>).</summary>
  SealRecheck = 1,
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
  /// <summary>True once the full-history compare's schedule is registered on the temporal engine.</summary>
  public bool CronActive { get; set; }

  /// <summary>True once the sealed-history re-check's schedule is registered on the temporal engine (#2353).</summary>
  public bool SealRecheckCronActive { get; set; }
}
