#pragma warning disable CA1707 // Test method names can contain underscores

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// The idle ops-rate projection math: each session-enabled subscription costs up to
/// <c>MaxConcurrentSessions / SessionIdleTimeout</c> broker operations per second per consumer
/// instance while completely idle — every expiring accept is a billable namespace request. The
/// projection is the early-warning seam for the failure mode where the receive machinery alone
/// pins a namespace's shared request quota with zero messages flowing.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AsbOpsRateSelfCheck.cs</code-under-test>
public class AsbOpsRateSelfCheckTests {

  [Test]
  public async Task ProjectIdleOpsPerSecond_HairTriggerConfiguration_Projects200PerSubscriptionAsync() {
    // The configuration that saturates a Standard namespace at idle: 200 acceptors re-polling
    // every second. Adaptive acceptors disabled — this locks the legacy standing-army math.
    var options = new AzureServiceBusOptions {
      EnableSessions = true,
      EnableAdaptiveAcceptors = false,
      MaxConcurrentSessions = 200,
      SessionIdleTimeout = TimeSpan.FromSeconds(1),
    };

    var projected = AsbOpsRateSelfCheck.ProjectIdleOpsPerSecond(options, sessionSubscriptionCount: 1);

    await Assert.That(projected).IsEqualTo(200d)
      .Because("200 concurrency slots each re-accepting once per second is 200 idle ops/sec per subscription");
  }

  [Test]
  public async Task ProjectIdleOpsPerSecond_TurnkeyDefaults_ScaleLinearlyWithSubscriptionsAsync() {
    var options = new AzureServiceBusOptions {
      EnableSessions = true,
      EnableAdaptiveAcceptors = false,
      MaxConcurrentSessions = 200,
      SessionIdleTimeout = TimeSpan.FromSeconds(60),
    };

    var projected = AsbOpsRateSelfCheck.ProjectIdleOpsPerSecond(options, sessionSubscriptionCount: 3);

    await Assert.That(projected).IsEqualTo(10d)
      .Because("200/60 ≈ 3.33 ops/sec per subscription × 3 subscriptions = 10 ops/sec");
  }

  [Test]
  public async Task ProjectIdleOpsPerSecond_SessionsDisabled_ProjectsZeroAsync() {
    var options = new AzureServiceBusOptions {
      EnableSessions = false,
      MaxConcurrentSessions = 200,
      SessionIdleTimeout = TimeSpan.FromSeconds(1),
    };

    var projected = AsbOpsRateSelfCheck.ProjectIdleOpsPerSecond(options, sessionSubscriptionCount: 5);

    await Assert.That(projected).IsEqualTo(0d)
      .Because("non-session processors long-poll; they do not churn accept operations at idle");
  }

  [Test]
  public async Task ProjectIdleOpsPerSecond_NoSubscriptions_ProjectsZeroAsync() {
    var options = new AzureServiceBusOptions();

    var projected = AsbOpsRateSelfCheck.ProjectIdleOpsPerSecond(options, sessionSubscriptionCount: 0);

    await Assert.That(projected).IsEqualTo(0d);
  }

  [Test]
  public async Task ProjectIdleOpsPerSecond_ZeroIdleTimeout_IsFiniteAndLargeAsync() {
    // A zero timeout must not divide by zero — it projects a finite, clearly-exceeding rate.
    var options = new AzureServiceBusOptions {
      EnableSessions = true,
      MaxConcurrentSessions = 10,
      SessionIdleTimeout = TimeSpan.Zero,
    };

    var projected = AsbOpsRateSelfCheck.ProjectIdleOpsPerSecond(options, sessionSubscriptionCount: 1);

    await Assert.That(double.IsFinite(projected)).IsTrue();
    await Assert.That(projected).IsGreaterThan(options.OpsRateWarningThresholdPerSecond);
  }

  [Test]
  public async Task Evaluate_AboveThreshold_ExceedsAsync() {
    var options = new AzureServiceBusOptions {
      EnableSessions = true,
      EnableAdaptiveAcceptors = false,
      MaxConcurrentSessions = 200,
      SessionIdleTimeout = TimeSpan.FromSeconds(1),
      OpsRateWarningThresholdPerSecond = 100,
    };

    var result = AsbOpsRateSelfCheck.Evaluate(options, sessionSubscriptionCount: 1);

    await Assert.That(result.ExceedsThreshold).IsTrue();
    await Assert.That(result.ProjectedIdleOpsPerSecond).IsEqualTo(200d);
    await Assert.That(result.ThresholdPerSecond).IsEqualTo(100d);
  }

  [Test]
  public async Task Evaluate_AtOrBelowThreshold_DoesNotExceedAsync() {
    var options = new AzureServiceBusOptions {
      EnableSessions = true,
      EnableAdaptiveAcceptors = false,
      MaxConcurrentSessions = 200,
      SessionIdleTimeout = TimeSpan.FromSeconds(60),
      OpsRateWarningThresholdPerSecond = 100,
    };

    var result = AsbOpsRateSelfCheck.Evaluate(options, sessionSubscriptionCount: 30);

    await Assert.That(result.ExceedsThreshold).IsFalse()
      .Because("30 subscriptions × 200/60 = 100 ops/sec sits AT the threshold; the warning fires only above it");
    await Assert.That(result.ProjectedIdleOpsPerSecond).IsEqualTo(100d);
  }

  // ===== Adaptive acceptors: the projection follows the floor, not the ceiling =====

  [Test]
  public async Task ProjectIdleOpsPerSecond_AdaptiveAcceptors_ProjectsFromTheFloorAsync() {
    // With adaptive acceptors (the default), the acceptor pool decays to the floor at idle by
    // construction — MaxConcurrentSessions is only the ceiling and must not drive the idle math.
    var options = new AzureServiceBusOptions {
      EnableSessions = true,
      EnableAdaptiveAcceptors = true,
      AcceptorFloor = 4,
      MaxConcurrentSessions = 200,
      SessionIdleTimeout = TimeSpan.FromSeconds(1),
    };

    var projected = AsbOpsRateSelfCheck.ProjectIdleOpsPerSecond(options, sessionSubscriptionCount: 1);

    await Assert.That(projected).IsEqualTo(4d)
      .Because("the idle cost of an adaptive pool is its floor — 4 acceptors / 1s, not the 200-slot ceiling");
  }

  [Test]
  public async Task ProjectIdleOpsPerSecond_AdaptiveFloorAboveCeiling_UsesTheCeilingAsync() {
    var options = new AzureServiceBusOptions {
      EnableSessions = true,
      EnableAdaptiveAcceptors = true,
      AcceptorFloor = 4,
      MaxConcurrentSessions = 2,
      SessionIdleTimeout = TimeSpan.FromSeconds(1),
    };

    var projected = AsbOpsRateSelfCheck.ProjectIdleOpsPerSecond(options, sessionSubscriptionCount: 1);

    await Assert.That(projected).IsEqualTo(2d)
      .Because("a floor above the ceiling clamps to the ceiling — the pool can never exceed MaxConcurrentSessions");
  }

  [Test]
  public async Task EvaluateAcceptorSlots_ProjectsFromTheLiveSlotTotalAsync() {
    // The adaptive wiring re-projects from the governors' CURRENT total, not from configuration:
    // a grown pool costs what it currently holds.
    var options = new AzureServiceBusOptions {
      EnableSessions = true,
      SessionIdleTimeout = TimeSpan.FromSeconds(1),
      OpsRateWarningThresholdPerSecond = 100,
    };

    var result = AsbOpsRateSelfCheck.EvaluateAcceptorSlots(options, totalAcceptorSlots: 150);

    await Assert.That(result.ProjectedIdleOpsPerSecond).IsEqualTo(150d);
    await Assert.That(result.ExceedsThreshold).IsTrue();
  }

  [Test]
  public async Task EvaluateAcceptorSlots_SessionsDisabledOrNoSlots_ProjectsZeroAsync() {
    var sessionless = new AzureServiceBusOptions { EnableSessions = false };
    var idle = new AzureServiceBusOptions { EnableSessions = true };

    await Assert.That(AsbOpsRateSelfCheck.EvaluateAcceptorSlots(sessionless, 150).ProjectedIdleOpsPerSecond).IsEqualTo(0d);
    await Assert.That(AsbOpsRateSelfCheck.EvaluateAcceptorSlots(idle, 0).ProjectedIdleOpsPerSecond).IsEqualTo(0d);
  }

  // ===== Per-entity acceptor budget (per-namespace-inboxes seam) =====

  [Test]
  public async Task AcceptorCeilingForIdleOpsBudget_ShrinksAsSubscriptionCountsGrowAsync() {
    // A per-instance idle-ops budget of 100/sec at a 60s idle timeout buys 6,000 acceptor-slots
    // of idle churn; each subscription's share shrinks as the subscription count grows.
    var at25 = AsbOpsRateSelfCheck.AcceptorCeilingForIdleOpsBudget(
      idleOpsBudgetPerSecond: 100, subscriptionCount: 25, sessionIdleTimeout: TimeSpan.FromSeconds(60));
    var at50 = AsbOpsRateSelfCheck.AcceptorCeilingForIdleOpsBudget(
      idleOpsBudgetPerSecond: 100, subscriptionCount: 50, sessionIdleTimeout: TimeSpan.FromSeconds(60));

    await Assert.That(at25).IsEqualTo(240)
      .Because("100 ops/sec × 60s / 25 subscriptions = 240 acceptors per subscription");
    await Assert.That(at50).IsEqualTo(120)
      .Because("doubling the subscription count halves each subscription's share of the same budget");
  }

  [Test]
  public async Task AcceptorCeilingForIdleOpsBudget_FloorsAtOneAcceptorAsync() {
    var ceiling = AsbOpsRateSelfCheck.AcceptorCeilingForIdleOpsBudget(
      idleOpsBudgetPerSecond: 1, subscriptionCount: 1000, sessionIdleTimeout: TimeSpan.FromSeconds(1));

    await Assert.That(ceiling).IsEqualTo(1)
      .Because("every subscription needs at least one acceptor to make progress — a zero ceiling would starve it entirely");
  }

  [Test]
  public async Task AcceptorCeilingForIdleOpsBudget_NonPositiveSubscriptionCount_ThrowsAsync() {
    await Assert.That(() => AsbOpsRateSelfCheck.AcceptorCeilingForIdleOpsBudget(
        idleOpsBudgetPerSecond: 100, subscriptionCount: 0, sessionIdleTimeout: TimeSpan.FromSeconds(60)))
      .Throws<ArgumentOutOfRangeException>();
  }
}
