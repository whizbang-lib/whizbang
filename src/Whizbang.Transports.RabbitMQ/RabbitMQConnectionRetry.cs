using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// Handles RabbitMQ connection establishment with retry and exponential backoff.
/// </summary>
/// <docs>messaging/transports/rabbitmq#connection-retry</docs>
/// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQConnectionRetryTests.cs</tests>
public sealed partial class RabbitMQConnectionRetry {
  private readonly RabbitMQOptions _options;
  private readonly ILogger? _logger;

  /// <summary>
  /// Creates a new connection retry handler.
  /// </summary>
  /// <param name="options">RabbitMQ options containing retry configuration.</param>
  /// <param name="logger">Optional logger for retry attempts.</param>
  public RabbitMQConnectionRetry(RabbitMQOptions options, ILogger? logger = null) {
    ArgumentNullException.ThrowIfNull(options);
    _options = options;
    _logger = logger;
  }

  [LoggerMessage(Level = LogLevel.Debug, Message = "Attempting RabbitMQ connection (attempt {Attempt})")]
  private static partial void LogConnectionAttempt(ILogger logger, int attempt);

  [LoggerMessage(Level = LogLevel.Information, Message = "RabbitMQ connection established after {Attempt} attempts")]
  private static partial void LogConnectionEstablished(ILogger logger, int attempt);

  [LoggerMessage(Level = LogLevel.Error, Message = "Failed to connect to RabbitMQ after {MaxAttempts} initial attempts. Giving up.")]
  private static partial void LogConnectionFailed(ILogger logger, Exception exception, int maxAttempts);

  [LoggerMessage(Level = LogLevel.Warning, Message = "RabbitMQ connection attempt {Attempt} failed. Retrying in {DelayMs}ms...")]
  private static partial void LogRetrying(ILogger logger, Exception exception, int attempt, double delayMs);

  [LoggerMessage(Level = LogLevel.Warning, Message = "RabbitMQ connection still failing after {Attempt} attempts. Continuing to retry every {DelayMs}ms...")]
  private static partial void LogStillRetrying(ILogger logger, int attempt, double delayMs);

  /// <summary>
  /// Creates a RabbitMQ connection with retry and exponential backoff.
  /// </summary>
  /// <param name="connectionString">The RabbitMQ connection string.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>An open RabbitMQ connection.</returns>
  /// <exception cref="BrokerUnreachableException">Thrown when all retry attempts are exhausted.</exception>
  public async Task<IConnection> CreateConnectionWithRetryAsync(
      string connectionString,
      CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrEmpty(connectionString);

    var factory = new ConnectionFactory {
      Uri = new Uri(connectionString),
      AutomaticRecoveryEnabled = true
    };

    return await CreateConnectionWithRetryAsync(factory, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Creates a RabbitMQ connection with retry and exponential backoff using a provided factory.
  /// If RetryIndefinitely is true (default), retries forever until success or cancellation.
  /// </summary>
  /// <param name="factory">The connection factory to use.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>An open RabbitMQ connection.</returns>
  /// <exception cref="BrokerUnreachableException">Thrown when RetryIndefinitely is false and all initial attempts are exhausted.</exception>
  public async Task<IConnection> CreateConnectionWithRetryAsync(
      ConnectionFactory factory,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(factory);

    var currentDelay = _options.InitialRetryDelay;
    var attempt = 0;

    while (true) {
      attempt++;
      cancellationToken.ThrowIfCancellationRequested();

      try {
        _logConnectionAttempt(attempt);
        var connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        _logConnectionSuccess(attempt);
        return connection;
      } catch (BrokerUnreachableException ex) {
        _handleRetryOrRethrow(ex, attempt, currentDelay);
        await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false);
        currentDelay = CalculateNextDelay(currentDelay);
      }
    }
  }

  /// <summary>
  /// Logs a connection attempt if a logger is available.
  /// </summary>
  private void _logConnectionAttempt(int attempt) {
    if (_logger is not null) {
      LogConnectionAttempt(_logger, attempt);
    }
  }

  /// <summary>
  /// Logs a successful connection if it took more than one attempt.
  /// </summary>
  private void _logConnectionSuccess(int attempt) {
    if (attempt > 1 && _logger is not null) {
      LogConnectionEstablished(_logger, attempt);
    }
  }

  /// <summary>
  /// Handles retry logic: logs and optionally rethrows based on retry configuration.
  /// </summary>
  private void _handleRetryOrRethrow(BrokerUnreachableException ex, int attempt, TimeSpan currentDelay) {
    if (attempt <= _options.InitialRetryAttempts) {
      _logRetryAttempt(ex, attempt, currentDelay);
    } else if (!_options.RetryIndefinitely) {
      _logAndRethrowConnectionFailure(ex);
    } else {
      _logIndefiniteRetry(attempt, currentDelay);
    }
  }

  /// <summary>
  /// Logs a retry attempt during the initial retry window.
  /// </summary>
  private void _logRetryAttempt(BrokerUnreachableException ex, int attempt, TimeSpan currentDelay) {
    if (_logger is not null) {
      LogRetrying(_logger, ex, attempt, currentDelay.TotalMilliseconds);
    }
  }

  /// <summary>
  /// Logs the final failure and rethrows when not retrying indefinitely.
  /// </summary>
  private void _logAndRethrowConnectionFailure(BrokerUnreachableException ex) {
    if (_logger is not null) {
      LogConnectionFailed(_logger, ex, _options.InitialRetryAttempts);
    }
    ExceptionDispatchInfo.Throw(ex);
  }

  /// <summary>
  /// Logs periodic status updates during indefinite retry.
  /// </summary>
  private void _logIndefiniteRetry(int attempt, TimeSpan currentDelay) {
    if (_logger is not null && attempt % 10 == 0) {
      LogStillRetrying(_logger, attempt, currentDelay.TotalMilliseconds);
    }
  }

  /// <summary>
  /// Calculates the next retry delay using exponential backoff.
  /// </summary>
  /// <param name="currentDelay">The current delay.</param>
  /// <returns>The next delay, capped at MaxRetryDelay.</returns>
  internal TimeSpan CalculateNextDelay(TimeSpan currentDelay) {
    var nextDelay = TimeSpan.FromTicks((long)(currentDelay.Ticks * _options.BackoffMultiplier));

    // Cap at max delay
    if (nextDelay > _options.MaxRetryDelay) {
      return _options.MaxRetryDelay;
    }

    return nextDelay;
  }
}
