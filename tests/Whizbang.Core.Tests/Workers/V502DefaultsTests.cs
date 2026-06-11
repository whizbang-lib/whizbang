using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// v0.502 sane-defaults regression locks.
///
/// <para>
/// Two production-impacting defaults were changed in v0.502 after a consumer hit pathological
/// states caused by their prior values:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="InboxDispatchWorkerOptions.MaxInboxAttempts"/> was
///   <c>null</c> (= infinite retry); now <c>10</c>. A pod observed 25k stuck inbox rows
///   with one row at <c>attempts == 114</c> because dead-lettering was a hidden opt-in.</description></item>
///   <item><description><see cref="ClaimWorkerOptions.NotifyHealthyPollingIntervalMilliseconds"/>
///   was <c>null</c> (= use the tight 250 ms baseline always); now <c>30000</c>. With
///   LISTEN/NOTIFY healthy, work-pickup latency is already sub-second; the tight poll
///   produced ~4 claim_work calls/sec/pod of pure waste, generating constant WAL pressure
///   on Azure.</description></item>
/// </list>
///
/// <para>
/// These tests lock the values so a future "tidy the defaults" refactor can't silently
/// regress operators who depend on the safer behavior.
/// </para>
/// </summary>
public class V502DefaultsTests {

  [Test]
  public async Task MaxInboxAttempts_DefaultsToTenAsync() {
    var options = new InboxDispatchWorkerOptions();
    await Assert.That(options.MaxInboxAttempts).IsEqualTo(10)
      .Because("v0.502 changed the default from null (infinite retry) to 10 to prevent " +
               "permanently-failing handlers from accumulating wh_inbox rows indefinitely");
  }

  [Test]
  public async Task NotifyHealthyPollingIntervalMilliseconds_DefaultsToOneSecondAsync() {
    var options = new ClaimWorkerOptions();
    await Assert.That(options.NotifyHealthyPollingIntervalMilliseconds).IsEqualTo(1_000)
      .Because("v0.683 dropped the default from 30000 to 1000 because notify_instance_owners " +
               "emits zero per-instance NOTIFYs for streams not yet in wh_active_streams, leaving " +
               "new-stream first-event discovery on the safety-net path. production import 2026-06-11 " +
               "showed the 30 s default as a 30 s+ dispatch-to-processing delay on cold start. " +
               "Same fix applied to PerspectiveWorkerOptions in v0.681 — see release notes.");
  }

  [Test]
  public async Task BothNewDefaults_CanBeExplicitlyRestoredToPriorBehaviorAsync() {
    var inbox = new InboxDispatchWorkerOptions { MaxInboxAttempts = null };
    var claim = new ClaimWorkerOptions { NotifyHealthyPollingIntervalMilliseconds = null };
    await Assert.That(inbox.MaxInboxAttempts).IsNull()
      .Because("operators must be able to explicitly opt back into infinite retry if they need it");
    await Assert.That(claim.NotifyHealthyPollingIntervalMilliseconds).IsNull()
      .Because("operators must be able to explicitly opt back into tight always-polling");
  }

  [Test]
  public async Task MaxOutboxAttempts_DefaultsToTenAsync() {
    var options = new OutboxDrainWorkerOptions();
    await Assert.That(options.MaxOutboxAttempts).IsEqualTo(10)
      .Because("v0.502 slice C.4b parity with MaxInboxAttempts — outbox rows that keep " +
               "failing must also dead-letter eventually instead of accumulating forever");
  }

  [Test]
  public async Task MaxPerspectiveEventAttempts_DefaultsToTenAsync() {
    var options = new PerspectiveWorkerOptions();
    await Assert.That(options.MaxPerspectiveEventAttempts).IsEqualTo(10)
      .Because("symmetric with inbox/outbox defaults; perspective wire-up follows in a subsequent slice");
  }

  [Test]
  public async Task EnableSafetyNetPoll_DefaultsToTrueAsync() {
    var options = new ClaimWorkerOptions();
    await Assert.That(options.EnableSafetyNetPoll).IsTrue()
      .Because("safety-net poll stays on by default; NOTIFY-only mode is opt-in via setting to false");
  }
}
