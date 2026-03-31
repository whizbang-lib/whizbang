namespace Whizbang.Core.Resilience;

/// <summary>
/// States for the circuit breaker state machine.
/// </summary>
public enum CircuitBreakerState {
  /// <summary>
  /// Normal operation — operations are executed directly.
  /// </summary>
  Closed,

  /// <summary>
  /// Failures exceeded threshold — operations return fallback immediately.
  /// Transitions to HalfOpen after cooldown expires.
  /// </summary>
  Open,

  /// <summary>
  /// Testing recovery — one operation is allowed through.
  /// Success → Closed, Failure → Open (with escalated cooldown).
  /// </summary>
  HalfOpen
}
