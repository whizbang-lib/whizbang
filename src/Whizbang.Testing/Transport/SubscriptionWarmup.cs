using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Testing.Transport;

/// <summary>
/// Handles subscription warmup for transport integration tests.
/// </summary>
/// <remarks>
/// <para>
/// Transport subscriptions (especially Azure Service Bus) may not be immediately
/// ready to receive messages after SubscribeAsync returns. The processor needs
/// time to establish AMQP connections and register with the broker.
/// </para>
/// <para>
/// This class provides a reliable warmup pattern: keep sending test messages
/// until one is successfully received, confirming the subscription is ready.
/// </para>
/// </remarks>
public static class SubscriptionWarmup {
  private const string WARMUP_PREFIX = "warmup-";

  /// <summary>
  /// Default timeout for subscription warmup.
  /// </summary>
  public static readonly TimeSpan DefaultWarmupTimeout = TimeSpan.FromSeconds(30);

  /// <summary>
  /// Default delay between warmup message sends.
  /// </summary>
  public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(2);

  /// <summary>
  /// Default initial delay before starting warmup.
  /// </summary>
  public static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Generates a unique warmup ID for distinguishing warmup messages from test messages.
  /// </summary>
  public static string GenerateWarmupId() => $"{WARMUP_PREFIX}{Guid.NewGuid():N}";

  /// <summary>
  /// Checks if a content string is a warmup message.
  /// </summary>
  public static bool IsWarmupMessage(string? content) =>
    content?.StartsWith(WARMUP_PREFIX, StringComparison.Ordinal) == true;

  /// <summary>
  /// Creates a pair of awaiters that distinguish warmup from test messages.
  /// </summary>
  /// <typeparam name="TPayload">The message payload type.</typeparam>
  /// <param name="warmupId">The warmup ID to detect.</param>
  /// <param name="contentSelector">Function to extract content string from payload.</param>
  /// <returns>A tuple of (warmupAwaiter, testMessageAwaiter, combinedHandler).</returns>
  public static (
    SignalAwaiter WarmupAwaiter,
    MessageAwaiter<IMessageEnvelope> TestMessageAwaiter,
    Func<IMessageEnvelope, string?, CancellationToken, Task> Handler
  ) CreateDiscriminatingAwaiters<TPayload>(
    string warmupId,
    Func<TPayload, string> contentSelector
  ) where TPayload : class {
    var warmupAwaiter = new SignalAwaiter();
    var testAwaiter = new MessageAwaiter<IMessageEnvelope>(
      envelope => {
        if (envelope is IMessageEnvelope<TPayload> typed) {
          var content = contentSelector(typed.Payload);
          if (!content.Contains(warmupId)) {
            return envelope;
          }
        }
        return null;
      }
    );

    // Combined handler that dispatches to both awaiters
    Func<IMessageEnvelope, string?, CancellationToken, Task> combinedHandler = async (envelope, envelopeType, ct) => {
      // Check for warmup message
      if (envelope is IMessageEnvelope<TPayload> typed) {
        var content = contentSelector(typed.Payload);
        if (content.Contains(warmupId)) {
          warmupAwaiter.Signal();
        }
      }

      // Also check for test message
      await testAwaiter.Handler(envelope, envelopeType, ct);
    };

    return (warmupAwaiter, testAwaiter, combinedHandler);
  }

  /// <summary>
  /// Performs subscription warmup by sending messages until one is received.
  /// </summary>
  /// <typeparam name="TEnvelope">The envelope type.</typeparam>
  /// <param name="transport">The transport to publish through.</param>
  /// <param name="destination">The destination to publish to.</param>
  /// <param name="envelopeFactory">Factory to create warmup envelopes.</param>
  /// <param name="warmupAwaiter">Awaiter that completes when warmup message is received.</param>
  /// <param name="timeout">Maximum warmup time.</param>
  /// <param name="retryInterval">Interval between publish attempts.</param>
  /// <param name="initialDelay">Delay before first publish attempt.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="TimeoutException">Thrown if warmup doesn't complete within timeout.</exception>
  public static async Task WarmupAsync<TEnvelope>(
    ITransport transport,
    TransportDestination destination,
    Func<TEnvelope> envelopeFactory,
    SignalAwaiter warmupAwaiter,
    TimeSpan? timeout = null,
    TimeSpan? retryInterval = null,
    TimeSpan? initialDelay = null,
    CancellationToken cancellationToken = default
  ) where TEnvelope : IMessageEnvelope {
    timeout ??= DefaultWarmupTimeout;
    retryInterval ??= DefaultRetryInterval;
    initialDelay ??= DefaultInitialDelay;

    // Give the processor time to establish its connection
    await Task.Delay(initialDelay.Value, cancellationToken);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(timeout.Value);

    while (!warmupAwaiter.IsSignaled && !cts.Token.IsCancellationRequested) {
      var envelope = envelopeFactory();
      await transport.PublishAsync(envelope, destination, cancellationToken: cts.Token);

      // Wait for either warmup completion or retry interval
      try {
        await warmupAwaiter.WaitAsync(retryInterval.Value, cts.Token);
        return; // Success!
      } catch (TimeoutException) {
        // Retry
      }
    }

    if (!warmupAwaiter.IsSignaled) {
      throw new TimeoutException($"Subscription warmup timed out after {timeout}");
    }
  }
}

/// <summary>
/// A simple signal awaiter that completes when signaled once.
/// Thread-safe and uses RunContinuationsAsynchronously to prevent deadlocks.
/// </summary>
public sealed class SignalAwaiter {
  private readonly TaskCompletionSource<bool> _tcs =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>
  /// Gets whether the signal has been received.
  /// </summary>
  public bool IsSignaled => _tcs.Task.IsCompleted;

  /// <summary>
  /// Signals completion. Thread-safe and idempotent.
  /// </summary>
  public void Signal() => _tcs.TrySetResult(true);

  /// <summary>
  /// Waits for the signal.
  /// </summary>
  /// <param name="timeout">Maximum time to wait.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="TimeoutException">Thrown if not signaled within timeout.</exception>
  public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) {
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(timeout);

    try {
      await _tcs.Task.WaitAsync(cts.Token);
    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
      throw new TimeoutException($"Signal not received within {timeout}");
    }
  }
}
