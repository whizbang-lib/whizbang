using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The integrity side of the arbitration must hold the slot for the duration of its work.
/// </summary>
/// <remarks>
/// Exclusion only exists if the higher-priority activity actually claims the slot. If integrity
/// work never registers, the maintenance gate can only ever see "nothing running" and the two
/// collide exactly as before — a passing arbiter unit test beside two workers that never announce
/// themselves. These tests pin the scoped-hold contract the receptor relies on.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/HousekeepingCoordinator.cs</code-under-test>
[Category("Workers")]
public class HousekeepingIntegrityPriorityTests {

  [Test]
  public async Task IntegrityHoldsTheSlotForTheDurationOfItsWorkAsync() {
    var coordinator = new HousekeepingCoordinator();

    using (var hold = coordinator.BeginIntegrityScope()) {
      await Assert.That(hold.Granted).IsTrue();
      var sweep = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, new ServiceBacklog());
      await Assert.That(sweep.Granted).IsFalse()
        .Because("the hold must span the whole cycle — releasing at entry would leave the window "
               + "the exclusion exists to close");
    }

    var after = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, new ServiceBacklog());
    await Assert.That(after.Granted).IsTrue()
      .Because("disposing the scope has to return the slot, or the first integrity cycle after "
             + "startup would block cleanup forever");
  }

  [Test]
  public async Task TheSlotIsReturnedWhenIntegrityWorkThrowsAsync() {
    var coordinator = new HousekeepingCoordinator();

    try {
      using var hold = coordinator.BeginIntegrityScope();
      throw new InvalidOperationException("checkpoint failed");
    } catch (InvalidOperationException) { }

    var after = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, new ServiceBacklog());
    await Assert.That(after.Granted).IsTrue()
      .Because("a throwing checkpoint that keeps the slot would disable maintenance permanently, "
             + "and checkpoints run on a far tighter cadence than the sweep they would block");
  }

  [Test]
  public async Task AnUngrantedScopeDoesNotReleaseSomeoneElsesSlotAsync() {
    var coordinator = new HousekeepingCoordinator();
    using var first = coordinator.BeginIntegrityScope();

    using (var second = coordinator.BeginIntegrityScope()) {
      await Assert.That(second.Granted).IsFalse()
        .Because("a second concurrent cycle must not stack on the first");
    }

    var sweep = coordinator.TryBegin(HousekeepingCoordinator.Activity.Maintenance, new ServiceBacklog());
    await Assert.That(sweep.Granted).IsFalse()
      .Because("disposing the REFUSED scope must not hand away the slot the first one still holds "
             + "— that would silently reopen the overlap while both callers believe they are safe");
  }
}
