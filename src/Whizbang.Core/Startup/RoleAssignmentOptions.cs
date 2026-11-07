// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace Whizbang.Core.Startup;

/// <summary>
/// Tuning for role assignment: every duty is held as a liveness-tied assignment row with a lease
/// and an epoch (#2044). There is no other way to hold a duty, and no session advisory lock.
/// </summary>
/// <remarks>
/// <para>
/// The lease is <see cref="RenewInterval"/> times <see cref="MissedRenewalsBeforeLapse"/>, so one
/// slow renewal never costs the role. A holder renews from its own work loop, never from a timer.
/// Past its lease, a holder is judged by the one liveness rule, the same rule every other reader of
/// instance liveness asks (#2140): it keeps the role while the rule calls it live and it renewed
/// inside its <see cref="ProgressWindow"/>, so a holder busy with startup or one long unit of work
/// keeps its role, and a holder that is connected but stuck loses it within the window. A holder the
/// rule calls dead lapses at its lease. After an involuntary lapse, that instance may not win the
/// role back for <see cref="CooldownAfterLapse"/>, so a slow instance cannot bounce the role. A
/// graceful release carries no cool-down.
/// </para>
/// <para>
/// The migrator is held by assignment too: its vote is part of the schema bootstrap, so it exists
/// before the first migration, and migrator waiters watch the assignment row. An instance on an older
/// release that still holds a duty by its own session lock is seen through the holding it records,
/// judged by the one liveness rule, never through the lock.
/// </para>
/// <para>
/// Each duty may declare its own lease in <see cref="RoleLeases"/>. Whatever the lease, a holder that
/// marked the backend running its duty's statement is treated as live while that statement runs, so
/// one long statement does not lose the role.
/// </para>
/// </remarks>
/// <docs>proposals/duty-role-assignment</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/RoleAssignmentOptionsTests.cs</tests>
[Whizbang.Core.Configuration.RemovedConfigurationKey("Whizbang:Database:RoleAssignment:Enabled")]
[Whizbang.Core.Configuration.RemovedConfigurationKey("Whizbang:Database:RoleAssignment:HoldLegacySessionLock")]
public sealed class RoleAssignmentOptions {
  /// <summary>The migrator's default lease: a migration can spend longer between statements than a maintenance pass.</summary>
  public static readonly TimeSpan DefaultMigratorLease = TimeSpan.FromSeconds(30);

  /// <summary>How often a holder renews its lease. Default 5 seconds.</summary>
  public TimeSpan RenewInterval { get; set; } = TimeSpan.FromSeconds(5);

  /// <summary>How many renewals in a row may be missed before the lease lapses. Default 3; at least 2.</summary>
  public int MissedRenewalsBeforeLapse { get; set; } = 3;

  /// <summary>
  /// How long a holder the one liveness rule calls live may go without renewing (verifying from its
  /// work loop) before it loses the role: the role's progress check (#2140). Default 2 minutes.
  /// <see cref="TimeSpan.Zero"/> turns it off, and the lease alone decides, as before.
  /// </summary>
  /// <remarks>
  /// The window bounds how long a holder that is connected but stuck keeps its role. Inside it, a
  /// holder busy with a slow startup or one long unit of exclusive work keeps the role instead of
  /// lapsing at its lease and handing the role to another instance mid-work. A holder whose instance
  /// the rule calls dead (crashed, stopped, cut off from the database) lapses at its lease whatever
  /// the window. The window a holder declares is never shorter than its role's lease.
  /// </remarks>
  /// <docs>proposals/duty-role-assignment</docs>
  /// <tests>tests/Whizbang.Core.Tests/Startup/RoleAssignmentOptionsTests.cs:ProgressWindow_DefaultsToTwoMinutesAsync</tests>
  public TimeSpan ProgressWindow { get; set; } = TimeSpan.FromMinutes(2);

  /// <summary>
  /// The progress window <paramref name="role"/>'s holder declares: <see cref="TimeSpan.Zero"/> when
  /// <see cref="ProgressWindow"/> is off, else the longer of it and the role's lease (<see cref="LeaseFor"/>).
  /// </summary>
  /// <param name="role">The role.</param>
  /// <returns>The window; zero declares none.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Startup/RoleAssignmentOptionsTests.cs:ProgressWindowFor_IsNeverShorterThanTheRolesLease_AndZeroTurnsItOffAsync</tests>
  public TimeSpan ProgressWindowFor(string role) {
    if (ProgressWindow <= TimeSpan.Zero) {
      return TimeSpan.Zero;
    }
    var lease = LeaseFor(role);
    return lease > ProgressWindow ? lease : ProgressWindow;
  }

  /// <summary>
  /// How long an instance whose assignment lapsed must wait before it can be elected again.
  /// Default 15 seconds.
  /// </summary>
  public TimeSpan CooldownAfterLapse { get; set; } = TimeSpan.FromSeconds(15);

  /// <summary>
  /// How long owed duty work backs off after a failed attempt, doubled per further failure and
  /// capped at one hour. Default 30 seconds.
  /// </summary>
  public TimeSpan OwedWorkRetryBase { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// The duties the holder loop holds and the <c>roles</c> health component reports. Default:
  /// <see cref="StartupDuties.MAINTAINER"/> and <see cref="StartupDuties.MIGRATOR"/>; the Postgres
  /// driver adds the commit-order stamper's role while the stamper is enabled. A duty not listed is
  /// still held by assignment whenever it is asked for: every duty is a role (#2044).
  /// </summary>
  public ISet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal) {
    StartupDuties.MAINTAINER, StartupDuties.MIGRATOR,
  };

  /// <summary>
  /// A lease per duty, for a duty whose work can go longer than the default lease between renewals.
  /// A duty not listed is granted <see cref="Lease"/>, except the migrator, which is granted
  /// <see cref="DefaultMigratorLease"/> or <see cref="Lease"/>, whichever is longer.
  /// </summary>
  public IDictionary<string, TimeSpan> RoleLeases { get; } = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

  /// <summary>The default lease a holder is granted: <see cref="RenewInterval"/> × <see cref="MissedRenewalsBeforeLapse"/>.</summary>
  public TimeSpan Lease => RenewInterval * MissedRenewalsBeforeLapse;

  /// <summary>
  /// The lease <paramref name="role"/> is held for: its own from <see cref="RoleLeases"/>; else, for the
  /// migrator, the longer of <see cref="DefaultMigratorLease"/> and <see cref="Lease"/>; else <see cref="Lease"/>.
  /// </summary>
  /// <param name="role">The role.</param>
  /// <returns>The lease.</returns>
  public TimeSpan LeaseFor(string role) {
    if (RoleLeases.TryGetValue(role, out var lease)) {
      return lease;
    }
    return IsEpisodic(role) && DefaultMigratorLease > Lease ? DefaultMigratorLease : Lease;
  }

  /// <summary>Whether the holder loop holds <paramref name="duty"/> and the health component reports it.</summary>
  /// <param name="duty">The duty name.</param>
  /// <returns>True when the duty is in <see cref="Roles"/>.</returns>
  public bool Manages(string duty) => Roles.Contains(duty);

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
  /// renewals per lease, a per-duty lease shorter than two renewals, a negative cool-down, a negative
  /// progress window, or a non-positive retry base.</exception>
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
    if (ProgressWindow < TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(ProgressWindow), ProgressWindow, "The progress window cannot be negative; zero turns it off.");
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
