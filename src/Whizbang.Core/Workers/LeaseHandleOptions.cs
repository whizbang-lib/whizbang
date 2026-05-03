namespace Whizbang.Core.Workers;

/// <summary>
/// Configuration for <see cref="LeaseHandle"/> deadline behavior. Phase H step 9 slice 1.
/// </summary>
/// <docs>fundamentals/work-coordinator/lease-cancellation</docs>
public sealed class LeaseHandleOptions {
  /// <summary>
  /// How many seconds before the SQL lease's <c>lease_expiry</c> the in-process token cancels.
  /// Default 30 s — gives the dispatch worker enough time to catch the cancellation, run the
  /// failure path, and release the lease BEFORE the DB-side expiry fires (which would otherwise
  /// race with <c>claim_orphaned_*</c> potentially re-issuing the same row).
  /// </summary>
  public int LeaseGraceSeconds { get; set; } = 30;

  /// <summary>
  /// Cap on how many times <see cref="LeaseHandle.TryExtendDeadline"/> can succeed before the
  /// handle refuses further extensions. Default 6 — at the default 5-minute lease, that gives
  /// each dispatch up to ~30 minutes of extension budget before the cap surfaces a hung handler
  /// via lease expiry → claim_orphaned re-issue → attempts increment → eventual dead-letter.
  /// </summary>
  public int MaxRenewalsPerWork { get; set; } = 6;
}
