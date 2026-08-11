using System;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Namespace throttling (error 50009, <see cref="ServiceBusFailureReason.ServiceBusy"/>) governs
/// the whole namespace: when the broker says "wait 2 seconds", every concurrent session-accept
/// slot that keeps retrying AMPLIFIES the throttle — observed live as a fleet whose consumers
/// could not accept a single session while thousands of messages sat queued, because N services
/// × M subscriptions × MaxConcurrentSessions accept polls kept the namespace pinned. The policy
/// converts that into one polite, exponentially-backed pause per processor: pause on ServiceBusy,
/// double per consecutive throttle up to a cap, reset after quiet.
/// </summary>
/// <docs>transports/azure-service-bus</docs>
public class AsbThrottleBackoffPolicyTests {
  private static readonly TimeSpan _base = TimeSpan.FromSeconds(2);
  private static readonly TimeSpan _max = TimeSpan.FromSeconds(60);
  private static readonly TimeSpan _quiet = TimeSpan.FromMinutes(5);

  private static AsbThrottleBackoffPolicy _policy() => new(_base, _max, _quiet);

  [Test]
  public async Task FirstThrottle_PausesForTheBaseDelayAsync() {
    var policy = _policy();
    var now = DateTimeOffset.UtcNow;

    var pause = policy.RecordError(ServiceBusFailureReason.ServiceBusy, now);

    await Assert.That(pause).IsEqualTo(_base)
      .Because("the broker literally asked for a wait — the first pause honors it immediately");
  }

  [Test]
  public async Task ConsecutiveThrottles_DoublePerStreak_UpToTheCapAsync() {
    var policy = _policy();
    var now = DateTimeOffset.UtcNow;

    var p1 = policy.RecordError(ServiceBusFailureReason.ServiceBusy, now);
    var p2 = policy.RecordError(ServiceBusFailureReason.ServiceBusy, now + TimeSpan.FromSeconds(3));
    var p3 = policy.RecordError(ServiceBusFailureReason.ServiceBusy, now + TimeSpan.FromSeconds(8));

    await Assert.That(p1).IsEqualTo(TimeSpan.FromSeconds(2));
    await Assert.That(p2).IsEqualTo(TimeSpan.FromSeconds(4))
      .Because("a still-throttled namespace needs less pressure, not the same pressure again");
    await Assert.That(p3).IsEqualTo(TimeSpan.FromSeconds(8));

    // Walk the streak to the cap and assert it stops growing.
    TimeSpan? last = null;
    var t = now + TimeSpan.FromSeconds(9);
    for (var i = 0; i < 10; i++) {
      last = policy.RecordError(ServiceBusFailureReason.ServiceBusy, t);
      t += TimeSpan.FromSeconds(1);
    }
    await Assert.That(last).IsEqualTo(_max)
      .Because("an unbounded pause would turn a throttle blip into a self-inflicted outage");
  }

  [Test]
  public async Task NonThrottleErrors_NeverPauseAsync() {
    var policy = _policy();
    var now = DateTimeOffset.UtcNow;

    var pause = policy.RecordError(ServiceBusFailureReason.MessageLockLost, now);

    await Assert.That(pause).IsNull()
      .Because("only namespace pressure warrants withholding accepts — ordinary errors keep receiving");
  }

  [Test]
  public async Task QuietPeriod_ResetsTheStreakAsync() {
    var policy = _policy();
    var now = DateTimeOffset.UtcNow;
    policy.RecordError(ServiceBusFailureReason.ServiceBusy, now);
    policy.RecordError(ServiceBusFailureReason.ServiceBusy, now + TimeSpan.FromSeconds(3));

    var afterQuiet = policy.RecordError(
      ServiceBusFailureReason.ServiceBusy, now + TimeSpan.FromSeconds(3) + _quiet + TimeSpan.FromSeconds(1));

    await Assert.That(afterQuiet).IsEqualTo(_base)
      .Because("a throttle after a quiet stretch is a new incident, not a continuation");
  }

  [Test]
  public async Task PauseIsSingleFlight_WhileOnePauseIsPending_FurtherThrottlesDoNotStackAsync() {
    // The whole point: N concurrent accept slots hitting ServiceBusy together must produce ONE
    // pause, not N stacked pauses. TryBeginPause is the single-flight gate the transport holds
    // while the processor is stopped; concurrent throttle reports during it are absorbed.
    var policy = _policy();
    var now = DateTimeOffset.UtcNow;

    var first = policy.RecordError(ServiceBusFailureReason.ServiceBusy, now);
    await Assert.That(first).IsNotNull();
    await Assert.That(policy.TryBeginPause()).IsTrue()
      .Because("the first reporter owns the pause");

    var concurrent = policy.RecordError(ServiceBusFailureReason.ServiceBusy, now + TimeSpan.FromMilliseconds(50));
    await Assert.That(concurrent).IsNotNull();
    await Assert.That(policy.TryBeginPause()).IsFalse()
      .Because("sibling slots reporting the same throttle must not stack stop/start cycles");

    policy.EndPause();
    await Assert.That(policy.TryBeginPause()).IsTrue()
      .Because("after the pause completes, the next throttle may pause again");
  }
}
