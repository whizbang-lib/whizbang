namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Result of an idle ops-rate projection: what the current session configuration costs the
/// broker per second while this consumer is completely idle, and whether that projected cost
/// crosses the configured warning threshold.
/// </summary>
/// <docs>messaging/transports/azure-service-bus#ops-rate-self-check</docs>
public readonly record struct AsbOpsRateProjection(
  double ProjectedIdleOpsPerSecond,
  double ThresholdPerSecond,
  bool ExceedsThreshold);

/// <summary>
/// Projects the transport's worst-case IDLE broker-operation rate from its session
/// configuration. Session receive is pull-shaped at the API level: every concurrency slot whose
/// <see cref="AzureServiceBusOptions.SessionIdleTimeout"/> expires issues a fresh accept — a
/// billable namespace request — even when zero messages flow. Each session-enabled subscription
/// therefore costs up to <c>acceptor-slots / SessionIdleTimeout</c> operations per second per
/// consumer instance, at idle, invisibly to message-level metrics: the namespace's shared
/// request quota can be fully consumed while message throughput reads zero and nothing logs.
/// The self-check makes that spend visible at subscribe time, before a fleet multiplies it.
/// <para>
/// With adaptive acceptors (<see cref="AzureServiceBusOptions.EnableAdaptiveAcceptors"/>, the
/// default) the idle pool decays to <see cref="AzureServiceBusOptions.AcceptorFloor"/> by
/// construction, so the configuration-based projection uses the FLOOR — not the
/// <see cref="AzureServiceBusOptions.MaxConcurrentSessions"/> ceiling. The live wiring
/// re-projects from the governors' current slot total via <see cref="EvaluateAcceptorSlots"/>.
/// </para>
/// </summary>
/// <docs>messaging/transports/azure-service-bus#ops-rate-self-check</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AsbOpsRateSelfCheckTests.cs</tests>
public static class AsbOpsRateSelfCheck {
  /// <summary>
  /// Worst-case idle accept operations per second across <paramref name="sessionSubscriptionCount"/>
  /// session-enabled subscriptions under <paramref name="options"/>. Zero when sessions are
  /// disabled (non-session processors long-poll and do not churn accepts at idle). Uses the
  /// standing <see cref="AzureServiceBusOptions.MaxConcurrentSessions"/> per subscription, or
  /// the (clamped) <see cref="AzureServiceBusOptions.AcceptorFloor"/> when adaptive acceptors
  /// are enabled — the pool an adaptive subscription actually holds at idle.
  /// </summary>
  public static double ProjectIdleOpsPerSecond(AzureServiceBusOptions options, int sessionSubscriptionCount) {
    ArgumentNullException.ThrowIfNull(options);
    if (sessionSubscriptionCount <= 0) {
      return 0;
    }
    return ProjectIdleOpsPerSecondForAcceptorSlots(
      options, sessionSubscriptionCount * _idleAcceptorSlotsPerSubscription(options));
  }

  /// <summary>
  /// Idle accept operations per second for an explicit total of live acceptor slots — the seam
  /// the adaptive wiring projects through, since governors grow and decay per subscription.
  /// Zero when sessions are disabled or no slots exist.
  /// </summary>
  public static double ProjectIdleOpsPerSecondForAcceptorSlots(AzureServiceBusOptions options, int totalAcceptorSlots) {
    ArgumentNullException.ThrowIfNull(options);
    if (!options.EnableSessions || totalAcceptorSlots <= 0) {
      return 0;
    }

    // A zero/negative idle timeout would divide by zero; clamp to 1ms — the projection stays
    // finite and lands far over any sane threshold, which is exactly the right signal.
    var idleSeconds = Math.Max(options.SessionIdleTimeout.TotalSeconds, 0.001);
    return totalAcceptorSlots / idleSeconds;
  }

  /// <summary>
  /// Projects the idle ops rate and compares it against
  /// <see cref="AzureServiceBusOptions.OpsRateWarningThresholdPerSecond"/>.
  /// </summary>
  public static AsbOpsRateProjection Evaluate(AzureServiceBusOptions options, int sessionSubscriptionCount) {
    ArgumentNullException.ThrowIfNull(options);
    return _toProjection(options, ProjectIdleOpsPerSecond(options, sessionSubscriptionCount));
  }

  /// <summary>
  /// Projects the idle ops rate for an explicit live acceptor-slot total and compares it against
  /// <see cref="AzureServiceBusOptions.OpsRateWarningThresholdPerSecond"/>.
  /// </summary>
  public static AsbOpsRateProjection EvaluateAcceptorSlots(AzureServiceBusOptions options, int totalAcceptorSlots) {
    ArgumentNullException.ThrowIfNull(options);
    return _toProjection(options, ProjectIdleOpsPerSecondForAcceptorSlots(options, totalAcceptorSlots));
  }

  /// <summary>
  /// The per-subscription acceptor ceiling a per-instance idle-ops budget affords: with
  /// <paramref name="subscriptionCount"/> session-enabled subscriptions sharing
  /// <paramref name="idleOpsBudgetPerSecond"/> broker operations per second of idle churn, each
  /// subscription may hold at most <c>budget × idleTimeout / subscriptions</c> acceptor slots —
  /// the ceiling SHRINKS as subscription counts grow, which is what keeps per-contract-namespace
  /// inbox topologies (K subscriptions per service) inside a fixed namespace spend. Never less
  /// than 1: every subscription needs one acceptor to make progress.
  /// </summary>
  public static int AcceptorCeilingForIdleOpsBudget(
    double idleOpsBudgetPerSecond, int subscriptionCount, TimeSpan sessionIdleTimeout) {
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(subscriptionCount, 0);

    var idleSeconds = Math.Max(sessionIdleTimeout.TotalSeconds, 0.001);
    var perSubscription = (int)Math.Floor(idleOpsBudgetPerSecond * idleSeconds / subscriptionCount);
    return Math.Max(perSubscription, 1);
  }

  /// <summary>The idle acceptor slots one subscription holds under the given configuration.</summary>
  private static int _idleAcceptorSlotsPerSubscription(AzureServiceBusOptions options) =>
    options.EnableAdaptiveAcceptors
      ? Math.Max(1, Math.Min(options.AcceptorFloor, options.MaxConcurrentSessions))
      : options.MaxConcurrentSessions;

  private static AsbOpsRateProjection _toProjection(AzureServiceBusOptions options, double projected) {
    var threshold = options.OpsRateWarningThresholdPerSecond;
    return new AsbOpsRateProjection(projected, threshold, projected > threshold);
  }
}
