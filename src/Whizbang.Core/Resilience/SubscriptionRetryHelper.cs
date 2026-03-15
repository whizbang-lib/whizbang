using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

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
public static partial class SubscriptionRetryHelper {
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
        LogSubscriptionGivingUp(logger, destination.Address, options.InitialRetryAttempts, state.LastError);
        state.Status = SubscriptionStatus.Failed;
        return;
      }

      attempt++;
      cancellationToken.ThrowIfCancellationRequested();

      try {
        var subscription = await transport.SubscribeAsync(handler, destination, cancellationToken);
        state.Subscription = subscription;
        state.Status = SubscriptionStatus.Healthy;

        // Hook into disconnection event for immediate reconnection
        subscription.OnDisconnected += (sender, args) => {
          if (args.IsApplicationInitiated) {
            return; // Don't reconnect if application is shutting down
          }

          LogSubscriptionDisconnected(logger, destination.Address, args.Reason);

          // Mark as recovering and trigger immediate reconnection
          state.Status = SubscriptionStatus.Recovering;
          state.LastError = args.Exception;
          state.LastErrorTime = DateTimeOffset.UtcNow;

          // Fire-and-forget reconnection attempt
          // Use Task.Run to avoid blocking the event handler
          _ = Task.Run(async () => {
            try {
              // Small delay to allow transport to fully disconnect
              await Task.Delay(options.InitialRetryDelay, cancellationToken);

              // Attempt reconnection with retry logic
              await SubscribeWithRetryAsync(transport, destination, handler, state, options, logger, cancellationToken);
            } catch (OperationCanceledException) {
              // Shutdown - ignore
            } catch (Exception ex) {
              LogReconnectionFailed(logger, destination.Address, ex);
            }
          }, cancellationToken);
        };

        if (attempt == 1) {
          LogSubscriptionSuccess(logger, destination.Address, destination.RoutingKey ?? "#");
        } else {
          LogSubscriptionEstablished(logger, destination.Address, destination.RoutingKey ?? "#", attempt);
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
          LogSubscriptionFailed(logger, destination.Address, attempt, currentDelay.TotalMilliseconds, ex);
        } else if (attempt % 10 == 0) {
          // Indefinite retry phase - log less frequently
          LogSubscriptionStillFailing(logger, destination.Address, attempt, currentDelay.TotalMilliseconds);
        }

        await Task.Delay(currentDelay, cancellationToken);
        currentDelay = CalculateNextDelay(currentDelay, options);
      }
    }
  }

  // ==========================================================================
  // LoggerMessage definitions - source generated for performance
  // ==========================================================================

  [LoggerMessage(
    EventId = 1,
    Level = LogLevel.Debug,
    Message = "✓ Subscribed to {Destination} (routing key: {RoutingKey})"
  )]
  private static partial void LogSubscriptionSuccess(ILogger logger, string destination, string routingKey);

  [LoggerMessage(
    EventId = 2,
    Level = LogLevel.Debug,
    Message = "✓ Subscribed to {Destination} (routing key: {RoutingKey}) after {Attempt} attempts"
  )]
  private static partial void LogSubscriptionEstablished(ILogger logger, string destination, string routingKey, int attempt);

  [LoggerMessage(
    EventId = 3,
    Level = LogLevel.Warning,
    Message = "Subscription to {Destination} failed (attempt {Attempt}). Retrying in {DelayMs}ms..."
  )]
  private static partial void LogSubscriptionFailed(ILogger logger, string destination, int attempt, double delayMs, Exception ex);

  [LoggerMessage(
    EventId = 4,
    Level = LogLevel.Error,
    Message = "Subscription to {Destination} failed after {MaxAttempts} initial attempts. Giving up."
  )]
  private static partial void LogSubscriptionGivingUp(ILogger logger, string destination, int maxAttempts, Exception? ex);

  [LoggerMessage(
    EventId = 5,
    Level = LogLevel.Warning,
    Message = "Subscription to {Destination} still failing after {Attempt} attempts. Continuing to retry every {DelayMs}ms..."
  )]
  private static partial void LogSubscriptionStillFailing(ILogger logger, string destination, int attempt, double delayMs);

  [LoggerMessage(
    EventId = 6,
    Level = LogLevel.Warning,
    Message = "Subscription to {Destination} disconnected: {Reason}. Attempting immediate reconnection..."
  )]
  private static partial void LogSubscriptionDisconnected(ILogger logger, string destination, string reason);

  [LoggerMessage(
    EventId = 7,
    Level = LogLevel.Error,
    Message = "Failed to reconnect subscription to {Destination} after disconnection"
  )]
  private static partial void LogReconnectionFailed(ILogger logger, string destination, Exception ex);
}
