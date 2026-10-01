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
/// Role assignment is on by default. The migrator is held by assignment too: its vote is part of the
/// schema bootstrap, so it exists before the first migration, and migrator waiters watch the
/// assignment row as well as the duty's session lock. Turn the whole mechanism off with
/// <see cref="Enabled"/>, and every duty goes back to the session-lock elector.
/// </para>
/// <para>
/// Each duty may declare its own lease in <see cref="RoleLeases"/>. Whatever the lease, a holder that
/// marked the backend running its duty's statement is treated as live while that statement runs, so
/// one long statement does not lose the role.
/// </para>
/// </remarks>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/RoleAssignmentOptionsTests.cs</tests>
public sealed class RoleAssignmentOptions {
  /// <summary>The migrator's default lease: a migration can spend longer between statements than a maintenance pass.</summary>
  public static readonly TimeSpan DefaultMigratorLease = TimeSpan.FromSeconds(30);

  /// <summary>
  /// Whether duties are held by assignment at all. Default true. When false, the role elector
  /// delegates every duty to the session-lock elector, the holder loop holds nothing, and the
  /// <c>roles</c> health component has nothing to report.
  /// </summary>
  public bool Enabled { get; set; } = true;

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
  /// While true, a holder also holds the duty's legacy session advisory lock, so an instance still
  /// running the session-lock elector sees the duty as held, and a new instance defers to an old
  /// holder. Default false. Turn it on for a rolling deploy from a release where duties were held by
  /// session lock (every release before role assignment became the default): with it off, an old
  /// instance can take the session lock after a vote. The vote still refuses while an old holder is
  /// visible, and a holder steps aside within one renewal once it sees one, but for that renewal
  /// interval both may act.
  /// </summary>
  public bool HoldLegacySessionLock { get; set; }

  /// <summary>
  /// How long owed duty work backs off after a failed attempt, doubled per further failure and
  /// capped at one hour. Default 30 seconds.
  /// </summary>
  public TimeSpan OwedWorkRetryBase { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// The duties held by assignment. Default: <see cref="StartupDuties.MAINTAINER"/> and
  /// <see cref="StartupDuties.MIGRATOR"/>; the Postgres driver adds the commit-order stamper's role
  /// while the stamper is enabled.
  /// </summary>
  public ISet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal) {
    StartupDuties.MAINTAINER, StartupDuties.MIGRATOR,
  };

  /// <summary>
  /// A lease per duty, for a duty whose work can go longer than the default lease between renewals.
  /// A duty not listed is granted <see cref="Lease"/>. Default: the migrator, at
  /// <see cref="DefaultMigratorLease"/>.
  /// </summary>
  public IDictionary<string, TimeSpan> RoleLeases { get; } = new Dictionary<string, TimeSpan>(StringComparer.Ordinal) {
    [StartupDuties.MIGRATOR] = DefaultMigratorLease,
  };

  /// <summary>
  /// How an instance on the session-lock elector locks a role, by schema, for a role whose legacy
  /// lock is not the duty lock (the commit-order stamper's leader lock, for one). A role not listed
  /// uses the duty lock. Read only while bridged, or to see an old holder.
  /// </summary>
  public IDictionary<string, Func<string?, long>> LegacyLockKeys { get; } = new Dictionary<string, Func<string?, long>>(StringComparer.Ordinal);

  /// <summary>The default lease a holder is granted: <see cref="RenewInterval"/> × <see cref="MissedRenewalsBeforeLapse"/>.</summary>
  public TimeSpan Lease => RenewInterval * MissedRenewalsBeforeLapse;

  /// <summary>The lease <paramref name="role"/> is held for: its own from <see cref="RoleLeases"/>, else <see cref="Lease"/>.</summary>
  /// <param name="role">The role.</param>
  /// <returns>The lease.</returns>
  public TimeSpan LeaseFor(string role) => RoleLeases.TryGetValue(role, out var lease) ? lease : Lease;

  /// <summary>Whether <paramref name="duty"/> is held by assignment rather than delegated.</summary>
  /// <param name="duty">The duty name.</param>
  /// <returns>True when role assignment is <see cref="Enabled"/> and the duty is in <see cref="Roles"/>.</returns>
  public bool Manages(string duty) => Enabled && Roles.Contains(duty);

  /// <summary>
  /// Whether <paramref name="role"/> is held only while a task runs rather than continuously: the
  /// migrator holds its role for one migration and then releases it, so a vacancy is its normal
  /// state, and the holder loop never holds it (a standing holder would keep every later migration
  /// waiting on it).
  /// </summary>
  /// <param name="role">The role.</param>
  /// <returns>True for the migrator.</returns>
  public static bool IsEpisodic(string role) => string.Equals(role, StartupDuties.MIGRATOR, StringComparison.Ordinal);

  /// <summary>Throws when the options describe a configuration that cannot work.</summary>
  /// <exception cref="ArgumentOutOfRangeException">A non-positive renew interval, fewer than two
  /// renewals per lease, a per-duty lease shorter than two renewals, a negative cool-down, or a
  /// non-positive retry base.</exception>
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
    if (OwedWorkRetryBase <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(OwedWorkRetryBase), OwedWorkRetryBase, "The retry base must be positive.");
    }
    foreach (var (role, lease) in RoleLeases) {
      if (lease < RenewInterval * 2) {
        throw new ArgumentOutOfRangeException(nameof(RoleLeases), lease,
          $"The lease for role '{role}' must span at least two renewals ({RenewInterval * 2}).");
      }
    }
  }
}
