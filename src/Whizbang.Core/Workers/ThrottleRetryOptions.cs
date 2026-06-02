namespace Whizbang.Core.Workers;

/// <summary>
/// In-memory retry budget for broker-side throttling (Azure Service Bus <c>ServiceBusy</c>
/// 50009, RabbitMQ flow-control, etc.) detected via
/// <see cref="TransportFailureClassifier"/>.
/// </summary>
/// <remarks>
/// <para>
/// Why in-memory instead of letting the row return to the failure channel: the drainer
/// already holds the row's lease, the broker pause is typically short (sub-second to a few
/// seconds), and the alternative — release the lease, increment <c>attempts</c>, wait for
/// <c>claim_orphaned_outbox</c> to re-pick after <c>scheduled_for</c> exponential backoff —
/// adds tens of seconds of latency and prematurely burns down the dead-letter budget on
/// transient broker pressure that would have cleared with a 1-second sleep.
/// </para>
/// <para>
/// After the retry budget exhausts (i.e., the throttle is sustained beyond
/// <see cref="MaxAttempts"/> attempts spread across the backoff schedule) the result returns
/// to the caller as <see cref="Messaging.MessageFailureReason.Throttled"/> and follows the
/// regular failure path. Counts toward <c>attempts</c> at that point.
/// </para>
/// </remarks>
public sealed class ThrottleRetryOptions {
  /// <summary>
  /// Maximum in-memory retry attempts on throttle before returning failure to the caller.
  /// Includes the initial attempt — so a value of <c>5</c> means "the first try plus up to
  /// 4 retries." Default <c>5</c>.
  /// </summary>
  public int MaxAttempts { get; set; } = 5;

  /// <summary>
  /// Base delay before the first retry. Default <c>250 ms</c>.
  /// </summary>
  public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(250);

  /// <summary>
  /// Multiplicative growth factor applied to the delay after each retry. Default <c>2.0</c>
  /// (true exponential backoff).
  /// </summary>
  public double BackoffMultiplier { get; set; } = 2.0;

  /// <summary>
  /// Upper bound on the per-attempt delay regardless of multiplier growth.
  /// Default <c>4 s</c>. Total budget with defaults: 250 + 500 + 1000 + 2000 + 4000 = ~7.75s.
  /// </summary>
  public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(4);

  /// <summary>
  /// Computes the delay before retry attempt <paramref name="attemptNumber"/> (1-based —
  /// the first retry is attempt 1, since the very first call is the initial attempt).
  /// </summary>
  public TimeSpan ComputeDelay(int attemptNumber) {
    if (attemptNumber < 1) {
      return TimeSpan.Zero;
    }
    var ms = BaseDelay.TotalMilliseconds * Math.Pow(BackoffMultiplier, attemptNumber - 1);
    var capped = Math.Min(ms, MaxDelay.TotalMilliseconds);
    return TimeSpan.FromMilliseconds(capped);
  }
}
