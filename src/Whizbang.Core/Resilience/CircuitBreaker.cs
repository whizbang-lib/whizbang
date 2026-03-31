using Microsoft.Extensions.Logging;

namespace Whizbang.Core.Resilience;

/// <summary>
/// Generic circuit breaker with exponential backoff cooldown and success caching.
/// Wraps any async operation to prevent cascading failures during sustained outages.
/// </summary>
/// <typeparam name="TResult">The result type of the protected operation.</typeparam>
/// <remarks>
/// <para>
/// State machine: Closed → Open → HalfOpen → Closed (or back to Open).
/// </para>
/// <para>
/// When the circuit opens, the cooldown starts short (default 3s) and escalates
/// exponentially on repeated failures: 3s → 6s → 12s → 24s → ... up to MaxCooldownSeconds.
/// The cooldown resets when the circuit successfully closes.
/// </para>
/// <para>
/// Successful results are cached for SuccessCacheDurationSeconds to avoid
/// redundant calls during normal operation.
/// </para>
/// </remarks>
/// <docs>resilience/circuit-breaker</docs>
#pragma warning disable CA1001 // CircuitBreaker is long-lived (app lifetime); disposing the semaphore is unnecessary
public sealed partial class CircuitBreaker<TResult> {
#pragma warning restore CA1001
  private readonly CircuitBreakerOptions _options;
  private readonly ILogger _logger;
  private readonly SemaphoreSlim _lock = new(1, 1);

  private CircuitBreakerState _state = CircuitBreakerState.Closed;
  private int _consecutiveFailures;
  private int _consecutiveOpens;
  private double _currentCooldownSeconds;
  private DateTimeOffset _circuitOpenedAt;
  private DateTimeOffset? _lastSuccessAt;
  private TResult? _cachedResult;

  /// <summary>
  /// Creates a new circuit breaker with the specified options.
  /// </summary>
  public CircuitBreaker(CircuitBreakerOptions options, ILogger? logger = null) {
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    _currentCooldownSeconds = options.InitialCooldownSeconds;
  }

  /// <summary>
  /// Current state of the circuit breaker.
  /// </summary>
  public CircuitBreakerState State => _state;

  /// <summary>
  /// Number of consecutive failures since last success.
  /// </summary>
  public int ConsecutiveFailures => _consecutiveFailures;

  /// <summary>
  /// Current cooldown duration in seconds (escalates on repeated openings).
  /// </summary>
  public double CurrentCooldownSeconds => _currentCooldownSeconds;

  /// <summary>
  /// Executes the operation through the circuit breaker.
  /// Returns the fallback value when the circuit is open.
  /// </summary>
  public async Task<TResult> ExecuteAsync(
    Func<CancellationToken, Task<TResult>> operation,
    TResult fallbackValue,
    CancellationToken cancellationToken) {

    // Fast path: check cached success
    if (_state == CircuitBreakerState.Closed && _options.SuccessCacheDurationSeconds > 0 && _lastSuccessAt.HasValue) {
      var elapsed = DateTimeOffset.UtcNow - _lastSuccessAt.Value;
      if (elapsed.TotalSeconds < _options.SuccessCacheDurationSeconds) {
        return _cachedResult!;
      }
    }

    await _lock.WaitAsync(cancellationToken);
    try {
      return await _executeWithLockAsync(operation, fallbackValue, cancellationToken);
    } finally {
      _lock.Release();
    }
  }

  private async Task<TResult> _executeWithLockAsync(
    Func<CancellationToken, Task<TResult>> operation,
    TResult fallbackValue,
    CancellationToken cancellationToken) {

    var now = DateTimeOffset.UtcNow;

    // Re-check cache inside lock
    if (_state == CircuitBreakerState.Closed && _options.SuccessCacheDurationSeconds > 0 && _lastSuccessAt.HasValue) {
      var elapsed = now - _lastSuccessAt.Value;
      if (elapsed.TotalSeconds < _options.SuccessCacheDurationSeconds) {
        return _cachedResult!;
      }
    }

    switch (_state) {
      case CircuitBreakerState.Open: {
          var elapsed = now - _circuitOpenedAt;
          if (elapsed.TotalSeconds < _currentCooldownSeconds) {
            var remaining = _currentCooldownSeconds - elapsed.TotalSeconds;
            LogCircuitRejected(_logger, remaining);
            return fallbackValue;
          }
          // Cooldown expired — transition to half-open
          _state = CircuitBreakerState.HalfOpen;
          LogCircuitHalfOpen(_logger);
          return await _tryExecuteAsync(operation, fallbackValue, cancellationToken);
        }

      case CircuitBreakerState.HalfOpen:
        // Another call during half-open — return fallback (only one probe allowed)
        return fallbackValue;

      case CircuitBreakerState.Closed:
      default:
        return await _tryExecuteAsync(operation, fallbackValue, cancellationToken);
    }
  }

  private async Task<TResult> _tryExecuteAsync(
    Func<CancellationToken, Task<TResult>> operation,
    TResult fallbackValue,
    CancellationToken cancellationToken) {

    try {
      var result = await operation(cancellationToken);
      _onSuccess(result);
      return result;
    } catch (Exception ex) when (ex is not OutOfMemoryException) {
      _onFailure(ex);
      return fallbackValue;
    }
  }

  private void _onSuccess(TResult result) {
    if (_state == CircuitBreakerState.HalfOpen) {
      LogCircuitClosed(_logger);
    }
    _state = CircuitBreakerState.Closed;
    _consecutiveFailures = 0;
    _consecutiveOpens = 0;
    _currentCooldownSeconds = _options.InitialCooldownSeconds;
    _lastSuccessAt = DateTimeOffset.UtcNow;
    _cachedResult = result;
  }

  private void _onFailure(Exception exception) {
    _consecutiveFailures++;
    _lastSuccessAt = null; // Invalidate cache on failure
    _cachedResult = default;

    if (_state == CircuitBreakerState.HalfOpen) {
      // Half-open probe failed — re-open with escalated cooldown
      _consecutiveOpens++;
      _currentCooldownSeconds = Math.Min(
        _options.InitialCooldownSeconds * Math.Pow(_options.CooldownBackoffMultiplier, _consecutiveOpens),
        _options.MaxCooldownSeconds);
      _state = CircuitBreakerState.Open;
      _circuitOpenedAt = DateTimeOffset.UtcNow;
      LogCooldownEscalated(_logger, _currentCooldownSeconds, _consecutiveOpens);
      return;
    }

    if (_consecutiveFailures >= _options.FailureThreshold) {
      _state = CircuitBreakerState.Open;
      _circuitOpenedAt = DateTimeOffset.UtcNow;
      LogCircuitOpened(_logger, _consecutiveFailures, _currentCooldownSeconds);
    }
  }

  // Source-generated log messages for high-performance, AOT-compatible logging
  [LoggerMessage(Level = LogLevel.Warning, Message = "Circuit breaker OPENED after {failures} consecutive failures. Cooldown: {cooldownSeconds}s.")]
  private static partial void LogCircuitOpened(ILogger logger, int failures, double cooldownSeconds);

  [LoggerMessage(Level = LogLevel.Information, Message = "Circuit breaker half-open — testing operation...")]
  private static partial void LogCircuitHalfOpen(ILogger logger);

  [LoggerMessage(Level = LogLevel.Information, Message = "Circuit breaker CLOSED — operation restored.")]
  private static partial void LogCircuitClosed(ILogger logger);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Circuit breaker open, returning fallback ({remainingSeconds:F1}s until retry).")]
  private static partial void LogCircuitRejected(ILogger logger, double remainingSeconds);

  [LoggerMessage(Level = LogLevel.Information, Message = "Circuit breaker cooldown escalated to {cooldownSeconds}s (attempt {attempt}).")]
  private static partial void LogCooldownEscalated(ILogger logger, double cooldownSeconds, int attempt);
}
