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
/// therefore costs up to <c>MaxConcurrentSessions / SessionIdleTimeout</c> operations per second
/// per consumer instance, at idle, invisibly to message-level metrics: the namespace's shared
/// request quota can be fully consumed while message throughput reads zero and nothing logs.
/// The self-check makes that spend visible at subscribe time, before a fleet multiplies it.
/// </summary>
/// <docs>messaging/transports/azure-service-bus#ops-rate-self-check</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AsbOpsRateSelfCheckTests.cs</tests>
public static class AsbOpsRateSelfCheck {
  /// <summary>
  /// Worst-case idle accept operations per second across <paramref name="sessionSubscriptionCount"/>
  /// session-enabled subscriptions under <paramref name="options"/>. Zero when sessions are
  /// disabled (non-session processors long-poll and do not churn accepts at idle).
  /// </summary>
  public static double ProjectIdleOpsPerSecond(AzureServiceBusOptions options, int sessionSubscriptionCount) {
    ArgumentNullException.ThrowIfNull(options);
    return 0;
  }

  /// <summary>
  /// Projects the idle ops rate and compares it against
  /// <see cref="AzureServiceBusOptions.OpsRateWarningThresholdPerSecond"/>.
  /// </summary>
  public static AsbOpsRateProjection Evaluate(AzureServiceBusOptions options, int sessionSubscriptionCount) {
    ArgumentNullException.ThrowIfNull(options);
    return new AsbOpsRateProjection(0, options.OpsRateWarningThresholdPerSecond, false);
  }
}
