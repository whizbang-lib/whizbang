// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The one answer to "is fleet-wide background work this instance's to do?" (#2001). With role assignment managing
/// the maintainer duty, only its holder does it; without that (no duty loop, or a loop that does not manage the
/// duty) every instance does, because there is no holder to defer to.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/MaintainerDuty.cs</code-under-test>
/// <docs>operations/startup/capabilities-and-duties#the-maintainer-duty-governs-fleet-wide-work</docs>
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

  [Test]
  [Arguments(RunState.Paused, true)]
  [Arguments(RunState.Stopped, true)]
  [Arguments(RunState.Paused, false)]
  public async Task WhileTheRunPermitIsClosed_NoInstanceHasATurnAsync(RunState closed, bool holds) {
    // #2045: every fleet-wide maintenance worker and step asks this one question, so the run permit is obeyed here once
    // for all of them. A paused, stood-down or stopping instance does no fleet-wide work, even while it still holds the
    // maintainer duty in memory (the path releases the role, and the holder learns of it on its next pass).
    var holder = await MaintainerDutyHolders.ElectedAsync(grants: holds);
    var permit = new WhizbangRunPermit();
    await permit.ChangeAsync(closed, CancellationToken.None);
    await using var services = new ServiceCollection().AddSingleton(holder).AddSingleton(permit).BuildServiceProvider();

    await Assert.That(MaintainerDuty.IsThisInstancesTurn(services)).IsFalse();
  }

  [Test]
  public async Task WithoutRoleAssignment_AClosedPermit_StillHasNoTurnAsync() {
    var permit = new WhizbangRunPermit();
    await permit.ChangeAsync(RunState.Paused, CancellationToken.None);
    await using var services = new ServiceCollection().AddSingleton(permit).BuildServiceProvider();

    await Assert.That(MaintainerDuty.IsThisInstancesTurn(services)).IsFalse();
  }

  [Test]
  public async Task AnOpenPermit_LeavesTheAnswerToTheDutyAsync() {
    var holder = await MaintainerDutyHolders.ElectedAsync(grants: true);
    await using var services = new ServiceCollection().AddSingleton(holder).AddSingleton(new WhizbangRunPermit()).BuildServiceProvider();

    await Assert.That(MaintainerDuty.IsThisInstancesTurn(services)).IsTrue();
  }
}
