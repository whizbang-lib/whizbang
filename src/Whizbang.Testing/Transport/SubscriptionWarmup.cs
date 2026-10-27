// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;
using Whizbang.Core.Async;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Testing.Transport;

/// <summary>
/// Handles subscription warmup for transport integration tests.
/// </summary>
/// <remarks>
/// <para>
/// Warmup is one round trip through the real subscription: publish a warmup message and wait for
/// the subscriber to signal that it received it. It proves the subscriber is consuming before the
/// test publishes the message it asserts on, without waiting on the clock.
/// </para>
/// <para>
/// One publish is enough. Every transport's subscribe call returns only after its subscription is
/// durable on the broker (an Azure Service Bus subscription exists, a RabbitMQ queue is declared and
/// bound), so the warmup message waits there until the subscriber takes it. There is no initial
/// delay and no resend: the only wait is on the subscriber's signal, bounded by a timeout that fails
/// the test when the signal never comes.
/// </para>
/// </remarks>
public static class SubscriptionWarmup {
  private const string WARMUP_PREFIX = "warmup-";

  /// <summary>
  /// Default timeout for subscription warmup.
  /// </summary>
  public static readonly TimeSpan DefaultWarmupTimeout = TimeSpan.FromSeconds(30);

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
    async Task combinedHandler(IMessageEnvelope envelope, string? envelopeType, CancellationToken ct) {
      // Check for warmup message
      if (envelope is IMessageEnvelope<TPayload> typed) {
        var content = contentSelector(typed.Payload);
        if (content.Contains(warmupId)) {
          warmupAwaiter.Signal();
        }
      }

      // Also check for test message
      await testAwaiter.Handler(envelope, envelopeType, ct);
    }

    return (warmupAwaiter, testAwaiter, combinedHandler);
  }

  /// <summary>
  /// Publishes one warmup message and waits for the subscriber to signal that it arrived.
  /// </summary>
  /// <typeparam name="TEnvelope">The envelope type.</typeparam>
  /// <param name="transport">The transport to publish through.</param>
  /// <param name="destination">The destination to publish to.</param>
  /// <param name="envelopeFactory">Factory to create the warmup envelope.</param>
  /// <param name="warmupAwaiter">Awaiter the subscriber signals when it receives the warmup message.</param>
  /// <param name="timeout">How long to wait for the signal. Defaults to <see cref="DefaultWarmupTimeout"/>.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="TimeoutException">Thrown if the subscriber does not signal within the timeout.</exception>
  /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is canceled.</exception>
  /// <tests>tests/Whizbang.Testing.Component.Tests/Transport/SubscriptionWarmupTests.cs</tests>
  public static async Task WarmupAsync<TEnvelope>(
    ITransport transport,
    TransportDestination destination,
    Func<TEnvelope> envelopeFactory,
    SignalAwaiter warmupAwaiter,
    TimeSpan? timeout = null,
    CancellationToken cancellationToken = default
  ) where TEnvelope : IMessageEnvelope {
    cancellationToken.ThrowIfCancellationRequested();
    if (warmupAwaiter.IsSignaled) {
      return;
    }

    var budget = timeout ?? DefaultWarmupTimeout;
    await transport.PublishAsync(envelopeFactory(), destination, cancellationToken: cancellationToken);

    try {
      await warmupAwaiter.WaitAsync(budget, cancellationToken);
    } catch (TimeoutException ex) {
      throw new TimeoutException($"Subscription warmup timed out after {budget}", ex);
    }
  }
}

/// <summary>
/// A simple signal awaiter that completes when signaled once.
/// Thread-safe and uses RunContinuationsAsynchronously to prevent deadlocks.
/// </summary>
public sealed class SignalAwaiter : IAwaiterIdentity {
  private readonly TaskCompletionSource<bool> _tcs =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

  public Guid AwaiterId { get; } = TrackedGuid.New();

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
    await AsyncTimeoutHelper.WaitWithTimeoutAsync(
        _tcs.Task, timeout, $"Signal not received within {timeout}", cancellationToken);
  }
}
