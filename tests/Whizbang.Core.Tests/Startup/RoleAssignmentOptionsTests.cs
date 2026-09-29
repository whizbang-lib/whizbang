using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The tuning behind role assignment (#966): the lease is several renew intervals, so one slow
/// renewal never costs the role; the options refuse a configuration that would make every late
/// beat a lapse, and refuse the migrator duty, which stays on the session lock until its table
/// joins the bootstrap closure.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/RoleAssignmentOptions.cs</code-under-test>
[Category("Startup")]
public class RoleAssignmentOptionsTests {

  [Test]
  public async Task Defaults_ManageTheMaintainer_WithALeaseOfThreeRenewals_AndTheBridgeOnAsync() {
    var options = new RoleAssignmentOptions();

    await Assert.That(options.RenewInterval).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(options.MissedRenewalsBeforeLapse).IsEqualTo(3);
    await Assert.That(options.Lease).IsEqualTo(TimeSpan.FromSeconds(15));
    await Assert.That(options.CooldownAfterLapse).IsEqualTo(TimeSpan.FromSeconds(15));
    await Assert.That(options.HoldLegacySessionLock).IsTrue()
      .Because("mixed-version fleets must never both act, and old instances only see the session lock");
    await Assert.That(options.Manages(StartupDuties.MAINTAINER)).IsTrue();
    await Assert.That(options.Manages("some-other-duty")).IsFalse();
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
  public async Task Validate_RefusesTheMigratorDutyAsync() {
    var options = new RoleAssignmentOptions();
    options.Roles.Add(StartupDuties.MIGRATOR);

    await Assert.That(options.Validate).Throws<InvalidOperationException>()
      .Because("the migrator is elected before migrations run, and its waiters watch the session lock");
    await Assert.That(options.Manages(StartupDuties.MIGRATOR)).IsFalse();
  }
}
