using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Handles Azure Service Bus connection establishment with retry and exponential backoff.
/// </summary>
/// <docs>messaging/transports/azure-service-bus#connection-retry</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusConnectionRetryTests.cs</tests>
public sealed partial class AzureServiceBusConnectionRetry {
  private readonly AzureServiceBusOptions _options;
  private readonly ILogger? _logger;

  /// <summary>
  /// Creates a new connection retry handler.
  /// </summary>
  /// <param name="options">Azure Service Bus options containing retry configuration.</param>
  /// <param name="logger">Optional logger for retry attempts.</param>
  public AzureServiceBusConnectionRetry(AzureServiceBusOptions options, ILogger? logger = null) {
    ArgumentNullException.ThrowIfNull(options);
    _options = options;
    _logger = logger;
  }

  [LoggerMessage(Level = LogLevel.Debug, Message = "Attempting Azure Service Bus connection (attempt {Attempt})")]
  private static partial void LogConnectionAttempt(ILogger logger, int attempt);

  [LoggerMessage(Level = LogLevel.Information, Message = "Azure Service Bus connection established after {Attempt} attempts")]
  private static partial void LogConnectionEstablished(ILogger logger, int attempt);

  [LoggerMessage(Level = LogLevel.Error, Message = "Failed to connect to Azure Service Bus after {MaxAttempts} initial attempts. Giving up.")]
  private static partial void LogConnectionFailed(ILogger logger, Exception exception, int maxAttempts);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Azure Service Bus connection attempt {Attempt} failed. Retrying in {DelayMs}ms...")]
  private static partial void LogRetrying(ILogger logger, Exception exception, int attempt, double delayMs);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Azure Service Bus connection still failing after {Attempt} attempts. Continuing to retry every {DelayMs}ms...")]
  private static partial void LogStillRetrying(ILogger logger, int attempt, double delayMs);

  /// <summary>
  /// Creates and verifies an Azure Service Bus connection with retry and exponential backoff.
  /// If RetryIndefinitely is true (default), retries forever until success or cancellation.
  /// </summary>
  /// <param name="connectionString">The Azure Service Bus connection string.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A verified ServiceBusClient.</returns>
  /// <exception cref="ServiceBusException">Thrown when RetryIndefinitely is false and all initial attempts are exhausted.</exception>
  public async Task<ServiceBusClient> CreateClientWithRetryAsync(
      string connectionString,
      CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrEmpty(connectionString);

    var currentDelay = _options.InitialRetryDelay;
    var attempt = 0;

    while (true) {
      attempt++;
      cancellationToken.ThrowIfCancellationRequested();

      try {
        if (_logger is not null) {
          LogConnectionAttempt(_logger, attempt);
        }

        // Create client and admin client
        var client = new ServiceBusClient(connectionString);
        var adminClient = new ServiceBusAdministrationClient(connectionString);

        // Verify connectivity by getting namespace properties
        // This forces actual connection to Service Bus
        _ = await adminClient.GetNamespacePropertiesAsync(cancellationToken).ConfigureAwait(false);

        if (attempt > 1 && _logger is not null) {
          LogConnectionEstablished(_logger, attempt);
        }

        return client;
      } catch (Exception ex) when (ex is ServiceBusException || _isTransientException(ex)) {
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

  /// <summary>
  /// Determines if an exception is transient and should be retried.
  /// </summary>
  private static bool _isTransientException(Exception ex) {
    // Check for Azure request failures wrapped in AggregateException
    if (ex is AggregateException aggregateException) {
      return aggregateException.InnerExceptions.Any(inner =>
        inner is ServiceBusException ||
        inner is Azure.RequestFailedException);
    }

    return ex is Azure.RequestFailedException;
  }
}
