using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Report-only is bilateral: <see cref="RepairTraffic"/> names the two messages that carry repair (not
/// detection) and reads the opt-in the same way every seam does, so a service that opted down declines,
/// discards, and sweeps consistently.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/RepairTraffic.cs</code-under-test>
public class RepairTrafficTests {
  [Test]
  public async Task InboxMessageTypeNames_NameTheRequestAndTheBundle_NormalizedAsync() {
    var names = RepairTraffic.InboxMessageTypeNames;

    await Assert.That(names).Contains($"{typeof(RequestRedeliveryCommand).FullName}, Whizbang.Core");
    await Assert.That(names).Contains($"{typeof(RedeliveryComposite).FullName}, Whizbang.Core");
    await Assert.That(names.Count).IsEqualTo(2)
      .Because("detection traffic (checkpoints, manifests, gap reports) is never repair");
    foreach (var name in names) {
      await Assert.That(name).DoesNotContain("Version=")
        .Because("stores match the normalized name by containment, so the name must carry no assembly metadata");
    }
  }

  [Test]
  public async Task IsRepairEnabled_OnlyForAnExplicitAutoRepairOptInAsync() {
    await Assert.That(RepairTraffic.IsRepairEnabled(null)).IsFalse()
      .Because("absent options read as the default, which is report-only");
    await Assert.That(RepairTraffic.IsRepairEnabled(new StreamIntegrityOptions())).IsFalse();
    await Assert.That(RepairTraffic.IsRepairEnabled(
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly })).IsFalse();
    await Assert.That(RepairTraffic.IsRepairEnabled(
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped })).IsTrue();
  }
}
