namespace Whizbang.Core.Workers;

/// <summary>
/// One registered backup-tick delegate — the unit of work the
/// <see cref="BackupTickCoordinator"/> fires on every POLLING-state tick.
/// </summary>
/// <param name="Name">
/// Short identifier for diagnostics ("stamper-backstop", "scheduled-retry",
/// "orphan-discovery"). Logged on every tick so operators can see which
/// registered delegates fired in a given window.
/// </param>
/// <param name="Tick">
/// The async action to execute. Receives the coordinator's stopping token so
/// long-running ticks cancel cleanly on shutdown. Exceptions from this
/// delegate are caught, logged, and isolated — failure of one registered
/// tick must never prevent others from firing.
/// </param>
/// <param name="IsEnabled">
/// Per-tick enable predicate. Evaluated immediately before each tick fires
/// so a tick can be flipped on/off without re-registering (e.g., the stamper
/// backstop only fires while this pod holds the advisory lock).
/// </param>
/// <docs>fundamentals/work-coordinator/backup-tick-coordinator</docs>
/// <tests>Whizbang.Core.Tests/Workers/BackupTickRegistryTests.cs</tests>
public sealed record BackupTickRegistration(
  string Name,
  Func<CancellationToken, Task> Tick,
  Func<bool> IsEnabled
);

/// <summary>
/// In-process registry of backup ticks consumed by
/// <see cref="BackupTickCoordinator"/>. Slice 4 of zero-idle-polling.
/// </summary>
/// <remarks>
/// Backup ticks replace the per-concern <see cref="BackgroundService"/>
/// timers that pre-Slice-4 Whizbang used for "periodic backstop" work
/// (ScheduledRetryWorker's 10 s tick, PgCommitOrderStamperWorker's 250 ms
/// floor poll, etc.). The coordinator runs one tick cycle that iterates the
/// registry — adding a new periodic concern becomes a one-line registration
/// instead of a new worker type.
/// </remarks>
/// <docs>fundamentals/work-coordinator/backup-tick-coordinator</docs>
/// <tests>Whizbang.Core.Tests/Workers/BackupTickRegistryTests.cs</tests>
public interface IBackupTickRegistry {
  /// <summary>
  /// Registers a backup tick. Idempotent on (<paramref name="name"/>,
  /// <paramref name="tick"/>, <paramref name="isEnabled"/>) by reference —
  /// callers don't need to track whether they've already registered. Order
  /// of execution follows registration order; the coordinator iterates in
  /// the order returned by <see cref="Registrations"/>.
  /// </summary>
  void Register(string name, Func<CancellationToken, Task> tick, Func<bool> isEnabled);

  /// <summary>
  /// Snapshot of all currently-registered ticks. The
  /// <see cref="BackupTickCoordinator"/> reads this on every POLLING cycle.
  /// </summary>
  IReadOnlyList<BackupTickRegistration> Registrations { get; }
}
