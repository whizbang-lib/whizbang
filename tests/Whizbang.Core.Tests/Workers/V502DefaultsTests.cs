using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// v0.502 sane-defaults regression locks.
///
/// <para>
/// Two production-impacting defaults were changed in v0.502 after JDX hit pathological
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
  public async Task NotifyHealthyPollingIntervalMilliseconds_DefaultsToThirtySecondsAsync() {
    var options = new ClaimWorkerOptions();
    await Assert.That(options.NotifyHealthyPollingIntervalMilliseconds).IsEqualTo(30_000)
      .Because("v0.502 changed the default from null (always-tight 250 ms baseline) to 30000 " +
               "so NOTIFY-healthy environments don't pay the 4 polls/sec/pod tax for nothing");
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
}
