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
/// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerResilienceTests.cs</tests>
public static partial class SubscriptionRetryHelper {
  /// <summary>
  /// Groups subscription retry parameters that travel together through retry and reconnection logic.
  /// </summary>
  private readonly record struct SubscriptionContext(
    ITransport Transport,
    TransportDestination Destination,
    Func<IMessageEnvelope, string?, CancellationToken, Task> Handler,
    SubscriptionState State,
    SubscriptionResilienceOptions Options,
    ILogger Logger);
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

        var subscriptionCtx = new SubscriptionContext(transport, destination, handler, state, options, logger);
        _hookDisconnectionReconnect(subscription, subscriptionCtx, cancellationToken);
        _logSubscriptionSuccess(logger, destination, attempt);

        return; // Success!
      } catch (OperationCanceledException) {
        throw; // Don't retry on cancellation
      } catch (Exception ex) {
        _handleSubscriptionFailure(state, ex);
        _logRetryAttempt(logger, destination, attempt, currentDelay, options, ex);

        await Task.Delay(currentDelay, cancellationToken);
        currentDelay = CalculateNextDelay(currentDelay, options);
      }
    }
  }

  /// <summary>
  /// Hooks into subscription disconnection event for immediate reconnection.
  /// </summary>
  private static void _hookDisconnectionReconnect(
      ISubscription subscription,
      SubscriptionContext ctx,
      CancellationToken cancellationToken) {
    subscription.OnDisconnected += (sender, args) => {
      if (args.IsApplicationInitiated) {
        return;
      }

      LogSubscriptionDisconnected(ctx.Logger, ctx.Destination.Address, args.Reason);

      ctx.State.Status = SubscriptionStatus.Recovering;
      ctx.State.LastError = args.Exception;
      ctx.State.LastErrorTime = DateTimeOffset.UtcNow;

      _ = Task.Run(async () => {
        try {
          await Task.Delay(ctx.Options.InitialRetryDelay, cancellationToken);
          await SubscribeWithRetryAsync(ctx.Transport, ctx.Destination, ctx.Handler, ctx.State, ctx.Options, ctx.Logger, cancellationToken);
        } catch (OperationCanceledException) {
          // Shutdown - ignore
        } catch (Exception ex) {
          LogReconnectionFailed(ctx.Logger, ctx.Destination.Address, ex);
        }
      }, cancellationToken);
    };
  }

  /// <summary>
  /// Logs subscription success, differentiating first attempt from retries.
  /// </summary>
  private static void _logSubscriptionSuccess(ILogger logger, TransportDestination destination, int attempt) {
    if (attempt == 1) {
      LogSubscriptionSuccess(logger, destination.Address, destination.RoutingKey ?? "#");
    } else {
      LogSubscriptionEstablished(logger, destination.Address, destination.RoutingKey ?? "#", attempt);
    }
  }

  /// <summary>
  /// Updates subscription state on failure.
  /// </summary>
  private static void _handleSubscriptionFailure(SubscriptionState state, Exception ex) {
    state.LastError = ex;
    state.LastErrorTime = DateTimeOffset.UtcNow;
    state.Status = SubscriptionStatus.Recovering;
    state.IncrementAttempt();
  }

  /// <summary>
  /// Logs retry attempt based on phase (initial vs indefinite).
  /// </summary>
  private static void _logRetryAttempt(
      ILogger logger,
      TransportDestination destination,
      int attempt,
      TimeSpan currentDelay,
      SubscriptionResilienceOptions options,
      Exception ex) {
    if (attempt <= options.InitialRetryAttempts) {
      LogSubscriptionFailed(logger, destination.Address, attempt, currentDelay.TotalMilliseconds, ex);
    } else if (attempt % 10 == 0) {
      LogSubscriptionStillFailing(logger, destination.Address, attempt, currentDelay.TotalMilliseconds);
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
