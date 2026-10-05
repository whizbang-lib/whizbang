// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The tuning behind role assignment (#966): the lease is several renew intervals, so one slow
/// renewal never costs the role; each duty may declare its own lease; the options refuse a
/// configuration that would make every late beat a lapse. On by default, with the migrator held by
/// assignment and the bridge on.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/RoleAssignmentOptions.cs</code-under-test>
[Category("Startup")]
public class RoleAssignmentOptionsTests {

  [Test]
  public async Task Defaults_ManageTheMaintainerAndTheMigrator_WithALeaseOfThreeRenewals_AndTheBridgeOnAsync() {
    var options = new RoleAssignmentOptions();

    await Assert.That(options.Enabled).IsTrue();
    await Assert.That(options.RenewInterval).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(options.MissedRenewalsBeforeLapse).IsEqualTo(3);
    await Assert.That(options.Lease).IsEqualTo(TimeSpan.FromSeconds(15));
    await Assert.That(options.CooldownAfterLapse).IsEqualTo(TimeSpan.FromSeconds(15));
    await Assert.That(options.HoldLegacySessionLock).IsTrue()
      .Because("consumers upgrading from session-lock releases roll through this one, and old and new must never both act");
    await Assert.That(options.Manages(StartupDuties.MAINTAINER)).IsTrue();
    await Assert.That(options.Manages(StartupDuties.MIGRATOR)).IsTrue()
      .Because("the migrator's vote is part of the schema bootstrap, so it can be held by assignment");
    await Assert.That(options.Manages("some-other-duty")).IsFalse();
    await Assert.That(options.LegacyLockKeys).IsEmpty();
  }

  [Test]
  public async Task Disabled_ManagesNothingAsync() {
    var options = new RoleAssignmentOptions { Enabled = false };

    await Assert.That(options.Manages(StartupDuties.MAINTAINER)).IsFalse();
    await Assert.That(options.Manages(StartupDuties.MIGRATOR)).IsFalse();
  }

  [Test]
  public async Task LeaseFor_ADutyThatDeclaresItsOwn_IsThatLease_ElseTheDefaultAsync() {
    var options = new RoleAssignmentOptions();
    options.RoleLeases["slow-duty"] = TimeSpan.FromMinutes(5);

    await Assert.That(options.LeaseFor("slow-duty")).IsEqualTo(TimeSpan.FromMinutes(5));
    await Assert.That(options.LeaseFor(StartupDuties.MIGRATOR)).IsEqualTo(RoleAssignmentOptions.DefaultMigratorLease);
    await Assert.That(options.LeaseFor(StartupDuties.MAINTAINER)).IsEqualTo(options.Lease);
    var slow = new RoleAssignmentOptions { RenewInterval = TimeSpan.FromSeconds(30) };
    await Assert.That(slow.LeaseFor(StartupDuties.MIGRATOR)).IsEqualTo(slow.Lease)
      .Because("the migrator is never granted less than the default lease");
    await Assert.That(slow.Validate).ThrowsNothing();
  }

  [Test]
  public async Task Validate_RefusesADeclaredLeaseShorterThanTwoRenewalsAsync() {
    var options = new RoleAssignmentOptions();
    options.RoleLeases["hasty"] = TimeSpan.FromSeconds(9);

    await Assert.That(options.Validate).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task IsEpisodic_OnlyForTheMigratorAsync() {
    await Assert.That(RoleAssignmentOptions.IsEpisodic(StartupDuties.MIGRATOR)).IsTrue();
    await Assert.That(RoleAssignmentOptions.IsEpisodic(StartupDuties.MAINTAINER)).IsFalse();
  }

  [Test]
  public async Task Lease_FollowsTheRenewIntervalAndTheMissedRenewalCountAsync() {
    var options = new RoleAssignmentOptions {
      RenewInterval = TimeSpan.FromSeconds(2),
      MissedRenewalsBeforeLapse = 4,
    };

    await Assert.That(options.Lease).IsEqualTo(TimeSpan.FromSeconds(8));
  }

  [Test]
  public async Task Validate_AcceptsTheDefaultsAsync() {
    await Assert.That(() => new RoleAssignmentOptions().Validate()).ThrowsNothing();
  }

  [Test]
  public async Task Validate_RefusesANonPositiveRenewIntervalAsync() {
    var options = new RoleAssignmentOptions { RenewInterval = TimeSpan.Zero };

    await Assert.That(options.Validate).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task Validate_RefusesALeaseOfFewerThanTwoRenewalsAsync() {
    var options = new RoleAssignmentOptions { MissedRenewalsBeforeLapse = 1 };

    await Assert.That(options.Validate).Throws<ArgumentOutOfRangeException>()
      .Because("a lease of one renewal turns every late beat into a lapse, which is flapping by construction");
  }

  [Test]
  public async Task Validate_RefusesANegativeCooldownAsync() {
    var options = new RoleAssignmentOptions { CooldownAfterLapse = TimeSpan.FromSeconds(-1) };

    await Assert.That(options.Validate).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task Validate_RefusesANonPositiveOwedWorkRetryBaseAsync() {
    var options = new RoleAssignmentOptions { OwedWorkRetryBase = TimeSpan.Zero };

    await Assert.That(new RoleAssignmentOptions().OwedWorkRetryBase).IsEqualTo(TimeSpan.FromSeconds(30));
    await Assert.That(options.Validate).Throws<ArgumentOutOfRangeException>();
  }

}
