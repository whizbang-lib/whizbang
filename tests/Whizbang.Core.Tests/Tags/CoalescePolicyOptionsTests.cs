using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Tags;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="CoalescePolicyOptions"/> — the per-tag coalesce policy knobs.
/// The defaults mirror the audit-ship knobs on SystemEventOptions (15 / 120 / 500 / Independent)
/// so the built-in audit binding and a host-declared binding describe the same shipped behavior.
/// </summary>
[Category("Core")]
[Category("Tags")]
public class CoalescePolicyOptionsTests {
  [Test]
  public async Task Defaults_MirrorTheAuditShipKnobsAsync() {
    var options = new CoalescePolicyOptions();

    await Assert.That(options.SlideSeconds).IsEqualTo(15)
      .Because("the default quiet window must match SystemEventOptions.AuditShipSlideSeconds");
    await Assert.That(options.MaxDelaySeconds).IsEqualTo(120)
      .Because("the default freshness cap must match SystemEventOptions.AuditShipMaxDelaySeconds");
    await Assert.That(options.MaxBatchCount).IsEqualTo(500)
      .Because("the default fold cap must match SystemEventOptions.AuditShipMaxBatchCount");
    await Assert.That(options.Atomicity).IsEqualTo(FanoutAtomicity.Independent)
      .Because("coalesce groups bundle self-contained records — one bad inner must never sink siblings by default");
  }

  [Test]
  public async Task Properties_CanBeConfiguredAsync() {
    var options = new CoalescePolicyOptions {
      SlideSeconds = 30,
      MaxDelaySeconds = 300,
      MaxBatchCount = 50,
      Atomicity = FanoutAtomicity.Atomic
    };

    await Assert.That(options.SlideSeconds).IsEqualTo(30);
    await Assert.That(options.MaxDelaySeconds).IsEqualTo(300);
    await Assert.That(options.MaxBatchCount).IsEqualTo(50);
    await Assert.That(options.Atomicity).IsEqualTo(FanoutAtomicity.Atomic);
  }

  [Test]
  public async Task SlideSecondsZero_IsTheDisabledSentinelAsync() {
    // Slide = 0 disables the group entirely: no CoalesceGroup stamp, no ScheduledFor floor,
    // exactly today's immediate individual shipping. Locked here as a settable value so the
    // bypass documented on SystemEventOptions.AuditShipSlideSeconds stays expressible per tag.
    var options = new CoalescePolicyOptions { SlideSeconds = 0 };

    await Assert.That(options.SlideSeconds).IsEqualTo(0);
  }
}
