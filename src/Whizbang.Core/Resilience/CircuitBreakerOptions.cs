namespace Whizbang.Core.Resilience;

/// <summary>
/// Configuration options for <see cref="CircuitBreaker{TResult}"/>.
/// Controls failure threshold, cooldown timing, backoff, and success caching.
/// </summary>
/// <docs>resilience/circuit-breaker</docs>
public class CircuitBreakerOptions {
  /// <summary>
  /// Number of consecutive failures before the circuit opens.
  /// Default: 5
  /// </summary>
  public int FailureThreshold { get; set; } = 5;

  /// <summary>
  /// Initial cooldown duration in seconds when the circuit first opens.
  /// Subsequent openings use exponential backoff: 3s → 6s → 12s → 24s → ...
  /// Default: 3
  /// </summary>
  public int InitialCooldownSeconds { get; set; } = 3;

  /// <summary>
  /// Multiplier applied to the cooldown on each consecutive circuit open.
  /// Default: 2.0
  /// </summary>
  public double CooldownBackoffMultiplier { get; set; } = 2.0;

  /// <summary>
  /// Maximum cooldown duration in seconds (caps the backoff).
  /// Default: 300 (5 minutes)
  /// </summary>
  public int MaxCooldownSeconds { get; set; } = 300;

  /// <summary>
  /// Duration in seconds to cache a successful result before re-executing the operation.
  /// Set to 0 to disable caching.
  /// Default: 5
  /// </summary>
  public int SuccessCacheDurationSeconds { get; set; } = 5;
}
