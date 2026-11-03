// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The one answer to "is fleet-wide background work this instance's to do?" (#2001). With role assignment managing
/// the maintainer duty, only its holder does it; without that (no duty loop, or a loop that does not manage the
/// duty) every instance does, because there is no holder to defer to.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/MaintainerDuty.cs</code-under-test>
public class MaintainerDutyTests {
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task WithTheMaintainerDutyAssigned_OnlyItsHolderHasATurnAsync(bool holds) {
    var holder = await MaintainerDutyHolders.ElectedAsync(grants: holds);
    await using var services = new ServiceCollection().AddSingleton(holder).BuildServiceProvider();

    await Assert.That(MaintainerDuty.IsThisInstancesTurn(services)).IsEqualTo(holds)
      .Because("with the duty assigned, the work is done once, by its holder");
  }

  [Test]
  public async Task WithoutRoleAssignment_EveryInstanceHasATurnAsync() {
    await using var services = new ServiceCollection().BuildServiceProvider();

    await Assert.That(MaintainerDuty.IsThisInstancesTurn(services)).IsTrue()
      .Because("with no duty loop there is no holder to defer to");
  }

  [Test]
  public async Task WithADutyLoopThatDoesNotManageTheMaintainerDuty_EveryInstanceHasATurnAsync() {
    var holder = await MaintainerDutyHolders.ElectedAsync(grants: false, role: StartupDuties.MIGRATOR);
    await using var services = new ServiceCollection().AddSingleton(holder).BuildServiceProvider();

    await Assert.That(MaintainerDuty.IsThisInstancesTurn(services)).IsTrue()
      .Because("the maintainer duty is not assigned, so no instance holds it");
  }

  [Test]
  public async Task ANullProvider_IsRefusedAsync() {
    await Assert.That(() => MaintainerDuty.IsThisInstancesTurn(null!)).Throws<ArgumentNullException>();
  }
}
