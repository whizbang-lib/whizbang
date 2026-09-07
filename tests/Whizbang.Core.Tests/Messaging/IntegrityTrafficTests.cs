using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A stream-integrity feature that is off leaves nothing behind: <see cref="IntegrityTraffic"/> maps
/// each control-plane message to the feature that produces or consumes it, so the maintenance sweep
/// discards exactly the rows of features that are off and never touches a feature that is on.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityTraffic.cs</code-under-test>
public class IntegrityTrafficTests {
  private static string _n(Type t) => $"{t.FullName}, Whizbang.Core";

  private static StreamIntegrityOptions _everythingOn() => new() {
    RepairMode = IntegrityRepairMode.AutoRepairCapped,
    CheckpointsEnabled = true,
    GapDetectionEnabled = true,
    AuditEnabled = true,
    PublishReportEvents = true,
  };

  [Test]
  public async Task EverythingOn_SweepsNothingAsync() {
    await Assert.That(IntegrityTraffic.InboxTypesToDiscard(_everythingOn())).IsEmpty()
      .Because("a feature that is on owns its traffic; the sweep must never race it");
    await Assert.That(IntegrityTraffic.OutboxTypesToDiscard(_everythingOn())).IsEmpty();
  }

  [Test]
  public async Task Defaults_SweepRepairTrafficAndUnpublishedReportsOnlyAsync() {
    var inbox = IntegrityTraffic.InboxTypesToDiscard(new StreamIntegrityOptions());
    var outbox = IntegrityTraffic.OutboxTypesToDiscard(new StreamIntegrityOptions());

    await Assert.That(inbox).IsEquivalentTo(RepairTraffic.InboxMessageTypeNames)
      .Because("report-only is the default, and detection is on, so only repair traffic is swept from the inbox");
    await Assert.That(outbox).Contains(_n(typeof(RequestRedeliveryCommand)));
    await Assert.That(outbox).Contains(_n(typeof(RedeliveryComposite)));
    await Assert.That(outbox).Contains(_n(typeof(IntegrityGapDetected)))
      .Because("publishing report events is opt-in; an unpublished report is a leftover");
    await Assert.That(outbox).Contains(_n(typeof(IntegrityDivergenceDetected)));
    await Assert.That(outbox).Contains(_n(typeof(PerspectiveCoverageGapDetected)));
    await Assert.That(outbox).DoesNotContain(_n(typeof(IntegrityCheckpoint)))
      .Because("checkpoints are on by default; their rows are live traffic");
    await Assert.That(outbox).DoesNotContain(_n(typeof(RequestIntegrityManifest)));
    await Assert.That(inbox).DoesNotContain(_n(typeof(IntegrityManifest)));
  }

  [Test]
  public async Task AbsentOptions_ReadAsTheDefaultsAsync() {
    await Assert.That(IntegrityTraffic.InboxTypesToDiscard(null))
      .IsEquivalentTo(IntegrityTraffic.InboxTypesToDiscard(new StreamIntegrityOptions()));
    await Assert.That(IntegrityTraffic.OutboxTypesToDiscard(null))
      .IsEquivalentTo(IntegrityTraffic.OutboxTypesToDiscard(new StreamIntegrityOptions()));
  }

  [Test]
  public async Task CheckpointsOff_SweepsUnpublishedCheckpointsFromTheOutboxOnlyAsync() {
    var o = _everythingOn();
    o.CheckpointsEnabled = false;

    await Assert.That(IntegrityTraffic.OutboxTypesToDiscard(o)).IsEquivalentTo([_n(typeof(IntegrityCheckpoint))]);
    await Assert.That(IntegrityTraffic.InboxTypesToDiscard(o)).IsEmpty()
      .Because("received checkpoints still feed gap detection, which is on");
  }

  [Test]
  public async Task GapDetectionOff_SweepsReceivedCheckpointsFromTheInboxOnlyAsync() {
    var o = _everythingOn();
    o.GapDetectionEnabled = false;

    await Assert.That(IntegrityTraffic.InboxTypesToDiscard(o)).IsEquivalentTo([_n(typeof(IntegrityCheckpoint))]);
    await Assert.That(IntegrityTraffic.OutboxTypesToDiscard(o)).IsEmpty()
      .Because("this service still publishes its own checkpoints for peers that detect");
  }

  [Test]
  public async Task AuditOff_SweepsOwnAsksAndReceivedAnswers_ButStillAnswersPeersAsync() {
    var o = _everythingOn();
    o.AuditEnabled = false;

    await Assert.That(IntegrityTraffic.OutboxTypesToDiscard(o)).IsEquivalentTo([_n(typeof(RequestIntegrityManifest))]);
    await Assert.That(IntegrityTraffic.InboxTypesToDiscard(o)).IsEquivalentTo([_n(typeof(IntegrityManifest))]);
    await Assert.That(IntegrityTraffic.InboxTypesToDiscard(o)).DoesNotContain(_n(typeof(RequestIntegrityManifest)))
      .Because("a service that does not audit can still be audited: peers' requests are answered");
  }

  [Test]
  public async Task ReportOnly_SweepsRepairTrafficFromBothTablesAsync() {
    var o = _everythingOn();
    o.RepairMode = IntegrityRepairMode.ReportOnly;

    await Assert.That(IntegrityTraffic.InboxTypesToDiscard(o)).IsEquivalentTo(RepairTraffic.InboxMessageTypeNames);
    await Assert.That(IntegrityTraffic.OutboxTypesToDiscard(o))
      .IsEquivalentTo([_n(typeof(RequestRedeliveryCommand)), _n(typeof(RedeliveryComposite))])
      .Because("an origin that opted down also drops the bundles and asks it minted before opting down");
  }

  [Test]
  public async Task Names_AreNormalized_NoAssemblyMetadataAsync() {
    var o = new StreamIntegrityOptions { CheckpointsEnabled = false, GapDetectionEnabled = false, AuditEnabled = false };
    foreach (var name in IntegrityTraffic.InboxTypesToDiscard(o).Concat(IntegrityTraffic.OutboxTypesToDiscard(o))) {
      await Assert.That(name).DoesNotContain("Version=")
        .Because("stores match the normalized name by containment");
      await Assert.That(name).Contains(", Whizbang.Core");
    }
  }
}
