using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Resilience;

/// <summary>
/// Decorator that wraps an <see cref="IDatabaseReadinessCheck"/> with a
/// <see cref="CircuitBreaker{TResult}"/> to prevent connection storms during
/// sustained database outages.
/// </summary>
/// <remarks>
/// When the database is unreachable, the circuit opens after consecutive failures
/// and returns <c>false</c> immediately (no network I/O). After the cooldown expires
/// (with exponential backoff), one probe connection is attempted.
/// </remarks>
/// <docs>resilience/circuit-breaker</docs>
public sealed class ResilientDatabaseReadinessCheck : IDatabaseReadinessCheck {
  private readonly IDatabaseReadinessCheck _inner;
  private readonly CircuitBreaker<bool> _circuitBreaker;

  /// <summary>
  /// Creates a resilient wrapper with default circuit breaker options.
  /// </summary>
  public ResilientDatabaseReadinessCheck(IDatabaseReadinessCheck inner, ILogger? logger = null)
    : this(inner, new CircuitBreakerOptions(), logger) {
  }

  /// <summary>
  /// Creates a resilient wrapper with custom circuit breaker options.
  /// </summary>
  public ResilientDatabaseReadinessCheck(
    IDatabaseReadinessCheck inner,
    CircuitBreakerOptions options,
    ILogger? logger = null) {

    _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    _circuitBreaker = new CircuitBreaker<bool>(options, logger);
  }

  /// <inheritdoc />
  public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
    return _circuitBreaker.ExecuteAsync(
      ct => _inner.IsReadyAsync(ct),
      fallbackValue: false,
      cancellationToken);
  }
}
