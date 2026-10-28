// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Resilience;

/// <summary>
/// Helper class for subscription retry logic with exponential backoff.
/// </summary>
/// <remarks>
/// <para>
/// This helper implements the same retry pattern as <c>RabbitMQConnectionRetry</c>:
/// exponential backoff with a maximum delay cap, and optional infinite retry.
/// </para>
/// <para>
/// A subscription that disconnects is reconnected after a delay that backs off across disconnect cycles: a
/// subscription that drops again before staying healthy for
/// <see cref="SubscriptionResilienceOptions.ReconnectBackoffResetAfter"/> makes the next reconnect wait longer, up to
/// <see cref="SubscriptionResilienceOptions.MaxRetryDelay"/>, so a destination that fails right after every successful
/// subscribe does not reconnect every <see cref="SubscriptionResilienceOptions.InitialRetryDelay"/> forever (#1840).
/// </para>
/// </remarks>
/// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportConsumerWorkerResilienceTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Resilience/SubscriptionRetryHelperDisconnectWindowTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportConsumerWorkerSubscriptionStatusChangedTests.cs:SubscriptionDisconnects_ReconnectDisposesTheReplacedSubscriptionAndShutdownDoesNotDisposeItAgainAsync</tests>
public static partial class SubscriptionRetryHelper {
  /// <summary>
  /// Groups subscription retry parameters that travel together through retry and reconnection logic.
  /// </summary>
  private readonly record struct SubscriptionContext(
    ITransport Transport,
    TransportDestination Destination,
    Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> BatchHandler,
    TransportBatchOptions BatchOptions,
    SubscriptionState State,
    SubscriptionResilienceOptions Options,
    ILogger Logger,
    TimeProvider TimeProvider);
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
  /// <param name="batchHandler">The batch message handler callback.</param>
  /// <param name="state">The subscription state to update.</param>
  /// <param name="options">Resilience options.</param>
  /// <param name="logger">Logger for retry attempts.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Public API surface: changing the signature is a binary/source break for NuGet consumers; each parameter is a distinct subscription concern (transport, destination, handler, batch options, state, resilience options, logger, cancellation).")]
  public static Task SubscribeWithRetryAsync(
    ITransport transport,
    TransportDestination destination,
    Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
    TransportBatchOptions batchOptions,
    SubscriptionState state,
    SubscriptionResilienceOptions options,
    ILogger logger,
    CancellationToken cancellationToken
  ) => SubscribeWithRetryAsync(transport, destination, batchHandler, batchOptions, state, options, logger, TimeProvider.System, cancellationToken);

  /// <summary>
  /// Attempts to subscribe to a destination with retry logic and exponential backoff, timing every wait on
  /// <paramref name="timeProvider"/>.
  /// </summary>
  /// <param name="transport">The transport to subscribe through.</param>
  /// <param name="destination">The destination to subscribe to.</param>
  /// <param name="batchHandler">The batch message handler callback.</param>
  /// <param name="batchOptions">The batch options the subscription is created with.</param>
  /// <param name="state">The subscription state to update.</param>
  /// <param name="options">Resilience options.</param>
  /// <param name="logger">Logger for retry attempts.</param>
  /// <param name="timeProvider">The clock the retry and reconnect delays wait on, and the error times are read from.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Resilience/SubscriptionRetryHelperReconnectBackoffTests.cs</tests>
  [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Public API surface: the overload of the method above that takes the clock; each parameter is a distinct subscription concern (transport, destination, handler, batch options, state, resilience options, logger, clock, cancellation).")]
  public static async Task SubscribeWithRetryAsync(
    ITransport transport,
    TransportDestination destination,
    Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
    TransportBatchOptions batchOptions,
    SubscriptionState state,
    SubscriptionResilienceOptions options,
    ILogger logger,
    TimeProvider timeProvider,
    CancellationToken cancellationToken
  ) {
    ArgumentNullException.ThrowIfNull(timeProvider);
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
        var subscription = await transport.SubscribeBatchAsync(
          batchHandler,
          destination,
          batchOptions,
          cancellationToken
        );
        // The handler is attached before the status is recorded, and the subscription's own record of a disconnect
        // is read once it is attached, so a disconnect at any point after the transport created the subscription
        // is acted on. One raised before the change to Healthy is held until that change is recorded, so Healthy
        // can never overwrite the Recovering it causes (#1831).
        state.Subscription = subscription;
        var watch = new DisconnectWatch(
          subscription,
          new SubscriptionContext(transport, destination, batchHandler, batchOptions, state, options, logger, timeProvider),
          cancellationToken);
        subscription.OnDisconnected += watch.OnDisconnected;
        if (subscription.Disconnection is { } missed) {
          watch.OnDisconnected(subscription, missed);
        }
        // Recorded before the change to Healthy and before a held disconnect is acted on, so the reconnect that
        // disconnect starts measures this subscription's healthy stretch, not the previous one's (#1840).
        state.MarkHealthy(timeProvider.GetUtcNow());
        state.Status = SubscriptionStatus.Healthy;
        _logSubscriptionSuccess(logger, destination, attempt);
        watch.Arm();

        return; // Success!
      } catch (OperationCanceledException) {
        throw; // Don't retry on cancellation
      } catch (Exception ex) {
        _handleSubscriptionFailure(state, ex, timeProvider);
        _logRetryAttempt(logger, destination, attempt, currentDelay, options, ex);

        await Task.Delay(currentDelay, timeProvider, cancellationToken);
        currentDelay = CalculateNextDelay(currentDelay, options);
      }
    }
  }

  /// <summary>
  /// Watches one subscription for its unexpected disconnect and turns the first one into Recovering and a reconnect.
  /// </summary>
  /// <remarks>
  /// A disconnect heard before <see cref="Arm"/> is held and acted on by <see cref="Arm"/>, which the helper calls
  /// after it records Healthy; one heard later is acted on at once. Only the first disconnect counts, and only while
  /// the state still holds this subscription: a second report, or a report from a subscription a later subscribe has
  /// replaced, must not start another reconnect or knock the current subscription into Recovering.
  /// </remarks>
  private sealed class DisconnectWatch(ISubscription subscription, SubscriptionContext ctx, CancellationToken cancellationToken) {
    private readonly Lock _gate = new();
    private bool _armed;
    private bool _seen;
    private SubscriptionDisconnectedEventArgs? _held;

    public void OnDisconnected(object? sender, SubscriptionDisconnectedEventArgs args) {
      if (args.IsApplicationInitiated) {
        return;
      }

      lock (_gate) {
        if (_seen) {
          return;
        }
        _seen = true;
        if (!_armed) {
          _held = args;
          return;
        }
      }

      _recover(args);
    }

    public void Arm() {
      SubscriptionDisconnectedEventArgs? held;
      lock (_gate) {
        _armed = true;
        held = _held;
      }

      if (held is not null) {
        _recover(held);
      }
    }

    private void _recover(SubscriptionDisconnectedEventArgs args) {
      if (!ReferenceEquals(ctx.State.Subscription, subscription)) {
        LogReplacedSubscriptionDisconnected(ctx.Logger, ctx.Destination.Address, args.Reason);
        return;
      }

      // A subscription that dropped soon after it was established backs the next reconnect off further than the last
      // one; one that stayed healthy long enough starts again from the initial delay (#1840).
      var now = ctx.TimeProvider.GetUtcNow();
      var delay = ctx.State.NextReconnectDelay(now, ctx.Options);
      LogSubscriptionDisconnected(ctx.Logger, ctx.Destination.Address, args.Reason, delay.TotalMilliseconds);

      // The error is recorded before the status, so an observer of the change to Recovering reads it.
      ctx.State.LastError = args.Exception;
      ctx.State.LastErrorTime = now;
      ctx.State.Status = SubscriptionStatus.Recovering;

      // The reconnect, not this handler, disposes the subscription it replaces: a transport raises the disconnect from
      // its own callback (an Azure Service Bus processor's error handler, a RabbitMQ channel's shutdown), and disposing
      // there would wait on the very callback it runs in.
      _ = Task.Run(async () => {
        try {
          _disposeReplaced();
          await Task.Delay(delay, ctx.TimeProvider, cancellationToken);
          await SubscribeWithRetryAsync(ctx.Transport, ctx.Destination, ctx.BatchHandler, ctx.BatchOptions, ctx.State, ctx.Options, ctx.Logger, ctx.TimeProvider, cancellationToken);
        } catch (OperationCanceledException) {
          // Shutdown - ignore
        } catch (Exception ex) {
          LogReconnectionFailed(ctx.Logger, ctx.Destination.Address, ex);
        }
      }, cancellationToken);
    }

    /// <summary>
    /// Disposes the disconnected subscription the reconnect is about to replace, exactly once (#1839).
    /// </summary>
    /// <remarks>
    /// It is disposed only when it is taken from the state here: connection recovery and shutdown take it the same
    /// atomic way, so whichever path takes it disposes it and the others leave it alone. A disposal that throws is
    /// logged and the reconnect goes on: the old subscription is already lost, and the destination still needs one.
    /// </remarks>
    private void _disposeReplaced() {
      subscription.OnDisconnected -= OnDisconnected;
      if (!ctx.State.TryReleaseSubscription(subscription)) {
        return;
      }

      try {
        subscription.Dispose();
      } catch (Exception ex) {
        LogReplacedSubscriptionDisposeFailed(ctx.Logger, ctx.Destination.Address, ex);
      }
    }
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
  private static void _handleSubscriptionFailure(SubscriptionState state, Exception ex, TimeProvider timeProvider) {
    // The status is written last, so an observer of the change to Recovering reads the error and the count.
    state.LastError = ex;
    state.LastErrorTime = timeProvider.GetUtcNow();
    state.IncrementAttempt();
    state.Status = SubscriptionStatus.Recovering;
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
    Message = "Subscription to {Destination} disconnected: {Reason}. Reconnecting in {DelayMs}ms..."
  )]
  private static partial void LogSubscriptionDisconnected(ILogger logger, string destination, string reason, double delayMs);

  [LoggerMessage(
    EventId = 7,
    Level = LogLevel.Error,
    Message = "Failed to reconnect subscription to {Destination} after disconnection"
  )]
  private static partial void LogReconnectionFailed(ILogger logger, string destination, Exception ex);

  [LoggerMessage(
    EventId = 8,
    Level = LogLevel.Debug,
    Message = "Subscription to {Destination} reported a disconnect ({Reason}) after a newer subscription replaced it; nothing to recover"
  )]
  private static partial void LogReplacedSubscriptionDisconnected(ILogger logger, string destination, string reason);

  [LoggerMessage(
    EventId = 9,
    Level = LogLevel.Warning,
    Message = "Disposing the replaced subscription to {Destination} failed; reconnecting anyway"
  )]
  private static partial void LogReplacedSubscriptionDisposeFailed(ILogger logger, string destination, Exception ex);
}
