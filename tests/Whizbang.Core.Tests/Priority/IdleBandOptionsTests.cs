using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Priority;

namespace Whizbang.Core.Tests.Priority;

/// <summary>
/// The idle band's drain options: the defaults that make withholding safe, and the precedence
/// between a type rule and a namespace rule.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#the-idle-band</docs>
[Category("Shard1")]
public class IdleBandOptionsTests {

  private sealed record AuditRecorded(string What);
  private sealed record SomethingElse(string What);

  [Test]
  public async Task Defaults_BoundHowLongWorkCanBeWithheldAsync() {
    var options = new IdleBandOptions();

    await Assert.That(options.TrickleAfter).IsEqualTo(TimeSpan.FromMinutes(30))
      .Because("trickling is the 'never starve me' half; thirty minutes is the longest a class "
        + "that opted in may wait while the service is busy.");
    await Assert.That(options.ForceFullDrainAfter).IsEqualTo(TimeSpan.FromHours(4))
      .Because("the floor underneath everything: a service that never goes quiet still empties the "
        + "band. Without it, withholding is a way to lose audit data silently.");
    await Assert.That(options.TrickleSlice).IsLessThan(50)
      .Because("a trickle that takes a full claim window is a burst. The slice being far below the "
        + "ordinary window is the whole distinction.");
  }

  [Test]
  public async Task Bounds_AreDurations_NotCountsAsync() {
    // A count of deferrals changes meaning when the poll interval moves -- the defect the
    // housekeeping budget carries. These are cadence-independent and must stay so.
    await Assert.That(new IdleBandOptions().TrickleAfter.GetType()).IsEqualTo(typeof(TimeSpan));
    await Assert.That(new IdleBandOptions().ForceFullDrainAfter.GetType()).IsEqualTo(typeof(TimeSpan));
  }

  [Test]
  public async Task WithholdingIsTheDefault_SoOptingOutIsExplicitAsync() {
    var options = new IdleBandOptions();
    // The key's exact rendering belongs to the shared helper, which is internal; registering a
    // type and reading back what the options recorded keeps this test on the public surface.
    var probe = new IdleBandOptions();
    probe.TrickleType<AuditRecorded>();
    var name = probe.TrickleTypes.Single();

    await Assert.That(options.Trickles(name)).IsFalse()
      .Because("a class that nobody opted in does not trickle. Withholding is the default for the "
        + "idle band, so a class escapes it by saying so rather than by accident.");
    await Assert.That(options.Trickles(null)).IsFalse()
      .Because("an unknown type must not read as opted in.");
  }

  [Test]
  public async Task ATypeRuleAndANamespaceRuleBothOptIn_AndTheNarrowerOneStillWinsAsync() {
    var options = new IdleBandOptions();
    options.TrickleType<AuditRecorded>();
    var audit = options.TrickleTypes.Single();
    var otherProbe = new IdleBandOptions();
    otherProbe.TrickleType<SomethingElse>();
    var other = otherProbe.TrickleTypes.Single();

    await Assert.That(options.Trickles(audit)).IsTrue();
    await Assert.That(options.Trickles(other)).IsFalse()
      .Because("a type rule opts in that type, not its neighbours.");

    options.TrickleNamespace(typeof(SomethingElse).Namespace!);
    await Assert.That(options.Trickles(other)).IsTrue()
      .Because("a namespace rule covers the types beneath it.");
  }

  [Test]
  public async Task LongerNamespacesAreConsideredFirstAsync() {
    // Registration order must not decide: a rule on the deeper namespace has to be the one that
    // answers for a type both could match, or the broader rule decides for types the narrower one
    // was written for.
    var options = new IdleBandOptions();
    options.TrickleNamespace("Acme").TrickleNamespace("Acme.Reporting.Detail");

    await Assert.That(options.TrickleNamespaces[0]).IsEqualTo("Acme.Reporting.Detail")
      .Because("the most specific namespace must be considered before the broader one.");
    await Assert.That(options.Trickles("Acme.Reporting.Detail.RowWritten")).IsTrue();
    await Assert.That(options.Trickles("AcmeCorp.Other.Thing")).IsFalse()
      .Because("a namespace rule matches on a segment boundary, not a string prefix: 'Acme' must "
        + "not swallow 'AcmeCorp'.");
  }
}
