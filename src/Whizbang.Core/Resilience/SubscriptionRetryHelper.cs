using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

#pragma warning disable CA1848 // Use LoggerMessage delegates for performance (not critical for retry logging)

namespace Whizbang.Core.Resilience;

/// <summary>
/// Helper class for subscription retry logic with exponential backoff.
/// </summary>
/// <remarks>
/// <para>
/// This helper implements the same retry pattern as <c>RabbitMQConnectionRetry</c>:
/// exponential backoff with a maximum delay cap, and optional infinite retry.
/// </para>
/// </remarks>
/// <docs>core-concepts/transport-consumer#subscription-resilience</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerResilienceTests.cs</tests>
public static class SubscriptionRetryHelper {
  /// <summary>
  /// Calculates the next delay using exponential backoff, capped at MaxRetryDelay.
  /// </summary>
  /// <param name="currentDelay">The current delay between retries.</param>
  /// <param name="options">Resilience options containing backoff settings.</param>
  /// <returns>The next delay, capped at MaxRetryDelay.</returns>
  public static TimeSpan CalculateNextDelay(TimeSpan currentDelay, SubscriptionResilienceOptions options) {
    var nextDelay = TimeSpan.FromTicks((long)(currentDelay.Ticks * options.BackoffMultiplier));
    return nextDelay > options.MaxRetryDelay ? options.MaxRetryDelay : nextDelay;
  }

  /// <summary>
  /// Attempts to subscribe to a destination with retry logic and exponential backoff.
  /// </summary>
  /// <param name="transport">The transport to subscribe through.</param>
  /// <param name="destination">The destination to subscribe to.</param>
  /// <param name="handler">The message handler callback.</param>
  /// <param name="state">The subscription state to update.</param>
  /// <param name="options">Resilience options.</param>
  /// <param name="logger">Logger for retry attempts.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public static async Task SubscribeWithRetryAsync(
    ITransport transport,
    TransportDestination destination,
    Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
    SubscriptionState state,
    SubscriptionResilienceOptions options,
    ILogger logger,
    CancellationToken cancellationToken
  ) {
    var currentDelay = options.InitialRetryDelay;
    var attempt = 0;

    while (true) {
      // Check if we've exhausted initial attempts and not retrying indefinitely
      if (attempt >= options.InitialRetryAttempts && !options.RetryIndefinitely) {
        _logSubscriptionGivingUp(logger, destination.Address, options.InitialRetryAttempts, state.LastError);
        state.Status = SubscriptionStatus.Failed;
        return;
      }

      attempt++;
      cancellationToken.ThrowIfCancellationRequested();

      try {
        var subscription = await transport.SubscribeAsync(handler, destination, cancellationToken);
        state.Subscription = subscription;
        state.Status = SubscriptionStatus.Healthy;

        if (attempt > 1) {
          _logSubscriptionEstablished(logger, destination.Address, attempt);
        }

        return; // Success!
      } catch (OperationCanceledException) {
        throw; // Don't retry on cancellation
      } catch (Exception ex) {
        state.LastError = ex;
        state.LastErrorTime = DateTimeOffset.UtcNow;
        state.Status = SubscriptionStatus.Recovering;
        state.IncrementAttempt();

        // Log based on attempt phase
        if (attempt <= options.InitialRetryAttempts) {
          // Initial retry phase - log each failure as warning
          _logSubscriptionFailed(logger, destination.Address, attempt, currentDelay.TotalMilliseconds, ex);
        } else if (attempt % 10 == 0) {
          // Indefinite retry phase - log less frequently
          _logSubscriptionStillFailing(logger, destination.Address, attempt, currentDelay.TotalMilliseconds);
        }

        await Task.Delay(currentDelay, cancellationToken);
        currentDelay = CalculateNextDelay(currentDelay, options);
      }
    }
  }

  #region Logging

  private static void _logSubscriptionEstablished(ILogger logger, string destination, int attempt) {
    logger.LogInformation(
      "Subscription to {Destination} established after {Attempt} attempts",
      destination,
      attempt
    );
  }

  private static void _logSubscriptionFailed(ILogger logger, string destination, int attempt, double delayMs, Exception ex) {
    logger.LogWarning(
      ex,
      "Subscription to {Destination} failed (attempt {Attempt}). Retrying in {DelayMs}ms...",
      destination,
      attempt,
      delayMs
    );
  }

  private static void _logSubscriptionGivingUp(ILogger logger, string destination, int maxAttempts, Exception? ex) {
    logger.LogError(
      ex,
      "Subscription to {Destination} failed after {MaxAttempts} initial attempts. Giving up.",
      destination,
      maxAttempts
    );
  }

  private static void _logSubscriptionStillFailing(ILogger logger, string destination, int attempt, double delayMs) {
    logger.LogWarning(
      "Subscription to {Destination} still failing after {Attempt} attempts. Continuing to retry every {DelayMs}ms...",
      destination,
      attempt,
      delayMs
    );
  }

  #endregion
}
