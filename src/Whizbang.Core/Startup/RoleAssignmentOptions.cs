using System;
using System.Collections.Generic;

namespace Whizbang.Core.Startup;

/// <summary>
/// Tuning for role assignment: duties held as a liveness-tied assignment row with an epoch,
/// rather than as a session advisory lock held for the whole tenure.
/// </summary>
/// <remarks>
/// <para>
/// The lease is <see cref="RenewInterval"/> times <see cref="MissedRenewalsBeforeLapse"/>, so one
/// slow renewal never costs the role: a holder lapses only after several renewals in a row fail to
/// arrive. A holder renews from its own work loop, never from a timer, so a holder that is stuck
/// stops renewing and lapses. After an involuntary lapse, that instance may not win the role back
/// for <see cref="CooldownAfterLapse"/>, so a slow instance cannot bounce the role. A graceful
/// release carries no cool-down.
/// </para>
/// <para>
/// The migrator duty is never managed here. It is elected before migrations run, so the
/// assignment table does not exist yet on a fresh database, and its waiters watch the duty's
/// session lock. It stays on the session-lock elector.
/// </para>
/// </remarks>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/RoleAssignmentOptionsTests.cs</tests>
public sealed class RoleAssignmentOptions {
  /// <summary>How often a holder renews its lease. Default 5 seconds.</summary>
  public TimeSpan RenewInterval { get; set; } = TimeSpan.FromSeconds(5);

  /// <summary>How many renewals in a row may be missed before the lease lapses. Default 3; at least 2.</summary>
  public int MissedRenewalsBeforeLapse { get; set; } = 3;

  /// <summary>
  /// How long an instance whose assignment lapsed must wait before it can be elected again.
  /// Default 15 seconds.
  /// </summary>
  public TimeSpan CooldownAfterLapse { get; set; } = TimeSpan.FromSeconds(15);

  /// <summary>
  /// While true (the default), a holder also holds the duty's legacy session advisory lock, so an
  /// instance still running the session-lock elector sees the duty as held, and a new instance
  /// defers to an old holder. Turn it off only once no instance older than the role-assignment
  /// release remains in the fleet: with it off, an old instance can take the session lock after a
  /// vote and both would act.
  /// </summary>
  public bool HoldLegacySessionLock { get; set; } = true;

  /// <summary>The duties held by assignment. Default: <see cref="StartupDuties.MAINTAINER"/>.</summary>
  public ISet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal) { StartupDuties.MAINTAINER };

  /// <summary>The lease a holder is granted: <see cref="RenewInterval"/> × <see cref="MissedRenewalsBeforeLapse"/>.</summary>
  public TimeSpan Lease => RenewInterval * MissedRenewalsBeforeLapse;

  /// <summary>Whether <paramref name="duty"/> is held by assignment rather than delegated.</summary>
  /// <param name="duty">The duty name.</param>
  /// <returns>True when the duty is in <see cref="Roles"/> and is not the migrator.</returns>
  public bool Manages(string duty) =>
    !string.Equals(duty, StartupDuties.MIGRATOR, StringComparison.Ordinal) && Roles.Contains(duty);

  /// <summary>Throws when the options describe a configuration that cannot work.</summary>
  /// <exception cref="ArgumentOutOfRangeException">A non-positive renew interval, fewer than two
  /// renewals per lease, or a negative cool-down.</exception>
  /// <exception cref="InvalidOperationException"><see cref="Roles"/> names the migrator duty.</exception>
  public void Validate() {
    if (RenewInterval <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(RenewInterval), RenewInterval, "The renew interval must be positive.");
    }
    if (MissedRenewalsBeforeLapse < 2) {
      throw new ArgumentOutOfRangeException(nameof(MissedRenewalsBeforeLapse), MissedRenewalsBeforeLapse,
        "A lease must span at least two renewals; with one, every late renewal is a lapse.");
    }
    if (CooldownAfterLapse < TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(CooldownAfterLapse), CooldownAfterLapse, "The cool-down cannot be negative.");
    }
    if (Roles.Contains(StartupDuties.MIGRATOR)) {
      throw new InvalidOperationException(
        $"The '{StartupDuties.MIGRATOR}' duty cannot be held by assignment: it is elected before migrations run, "
        + "and its waiters watch the duty's session lock. Remove it from RoleAssignmentOptions.Roles.");
    }
  }
}
