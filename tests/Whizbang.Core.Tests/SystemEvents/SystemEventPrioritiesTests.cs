using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Priority;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.SystemEvents.Security;

namespace Whizbang.Core.Tests.SystemEvents;

/// <summary>
/// Which band each kind of system event is written on.
/// </summary>
/// <remarks>
/// These were one blanket band until the idle band arrived. The distinction they now draw is not a
/// matter of taste: an idle-band record is deliberately not written while the service is busy, so
/// putting a security record there would withhold the evidence of exactly the load that most
/// warrants it.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/SystemEvents/SystemEventPriorities.cs</code-under-test>
/// <docs>fundamentals/messaging/message-priority#the-idle-band</docs>
[Category("SystemEvents")]
public class SystemEventPrioritiesTests {

  [Test]
  public async Task Auditing_IsOnTheIdleBandByDefaultAsync() {
    var options = new SystemEventOptions();

    foreach (var type in new[] { typeof(EventAudited), typeof(CommandAudited) }) {
      await Assert.That(WorkPriority.Bucket(SystemEventPriorities.For(type, options)))
        .IsEqualTo(WorkBucket.Idle)
        .Because($"{type.Name} is read days later if at all, and is most of what a bulk load generates");
    }
  }

  /// <summary>
  /// Audit follows the application's choice, so a deployment that reads its trail promptly can say
  /// so. The other kinds do not move with it.
  /// </summary>
  [Test]
  public async Task Auditing_FollowsTheConfiguredBand_AndNothingElseDoesAsync() {
    var options = new SystemEventOptions { AuditPriority = WorkPriority.INTERACTIVE };

    await Assert.That(SystemEventPriorities.For(typeof(EventAudited), options))
      .IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("the band is configuration; an application that needs its audit trail promptly says so");
    await Assert.That(SystemEventPriorities.For(typeof(AccessDenied), options))
      .IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the audit setting moves audit records, not every system event; a security record is "
        + "not an audit record and must not be dragged along by that switch in either direction");
  }

  /// <summary>
  /// No security event is ever on the withheld band, whatever audit is set to.
  /// </summary>
  [Test]
  public async Task SecurityEvents_AreNeverWithheldAsync() {
    var options = new SystemEventOptions { AuditPriority = WorkPriority.IDLE };
    var security = new[] {
      typeof(AccessDenied), typeof(AccessGranted),
      typeof(PermissionChanged), typeof(ScopeContextEstablished),
    };

    foreach (var type in security) {
      await Assert.That(SystemEventPriorities.IsSecurity(type)).IsTrue()
        .Because($"{type.Name} has to be classified, or it falls to a default nobody chose for it");
      await Assert.That(WorkPriority.Bucket(SystemEventPriorities.For(type, options)))
        .IsNotEqualTo(WorkBucket.Idle)
        .Because("the idle band is withheld while the service is busy, and a security record is most "
          + "needed about a load that looks like an attack");
    }
  }

  /// <summary>
  /// A system event type nobody classified is written on the background band, never the idle one.
  /// </summary>
  /// <remarks>
  /// The direction of the default is the whole point. Defaulting to idle would mean a system event
  /// added later is silently withheld under load, and nothing would say so.
  /// </remarks>
  [Test]
  public async Task AnUnclassifiedSystemEvent_DefaultsToBackground_NotIdleAsync() {
    var options = new SystemEventOptions();

    await Assert.That(SystemEventPriorities.For(typeof(UnclassifiedProbeEvent), options))
      .IsEqualTo(WorkPriority.BACKGROUND)
      .Because("a type nobody has thought about must not be withheld by default");
    await Assert.That(SystemEventPriorities.IsAudit(typeof(UnclassifiedProbeEvent))).IsFalse();
  }

  private sealed record UnclassifiedProbeEvent : ISystemEvent;
}
