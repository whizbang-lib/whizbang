using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// Handles RabbitMQ connection establishment with retry and exponential backoff.
/// </summary>
/// <docs>components/transports/rabbitmq#connection-retry</docs>
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
    Exception? lastException = null;
    var attempt = 0;

    while (true) {
      attempt++;
      cancellationToken.ThrowIfCancellationRequested();

      try {
        if (_logger is not null) {
          LogConnectionAttempt(_logger, attempt);
        }

        var connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (attempt > 1 && _logger is not null) {
          LogConnectionEstablished(_logger, attempt);
        }

        return connection;
      } catch (BrokerUnreachableException ex) {
        lastException = ex;

        // During initial retry phase, log each failure as warning
        if (attempt <= _options.InitialRetryAttempts) {
          if (_logger is not null) {
            LogRetrying(_logger, ex, attempt, currentDelay.TotalMilliseconds);
          }
        } else if (!_options.RetryIndefinitely) {
          // Not retrying indefinitely - throw after initial attempts
          if (_logger is not null) {
            LogConnectionFailed(_logger, ex, _options.InitialRetryAttempts);
          }
          throw;
        } else {
          // Retrying indefinitely - log less frequently (every 10 attempts)
          if (_logger is not null && attempt % 10 == 0) {
            LogStillRetrying(_logger, attempt, currentDelay.TotalMilliseconds);
          }
        }

        await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false);

        // Calculate next delay with exponential backoff (capped at MaxRetryDelay)
        currentDelay = CalculateNextDelay(currentDelay);
      }
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
