using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Testing.Transport;

/// <summary>
/// A high-level test harness for transport integration tests.
/// Provides simple APIs for common test patterns with proper async safety.
/// </summary>
/// <remarks>
/// <para>
/// This harness encapsulates the complexity of:
/// - Creating thread-safe TaskCompletionSource instances
/// - Warming up subscriptions before testing
/// - Proper timeout handling
/// - Cleanup and disposal
/// </para>
/// </remarks>
/// <typeparam name="TPayload">The message payload type being tested.</typeparam>
/// <remarks>
/// Creates a new transport test harness.
/// </remarks>
/// <param name="transport">The transport to test.</param>
/// <param name="envelopeFactory">
/// Factory to create envelopes. The string parameter is the content/warmup ID.
/// </param>
/// <param name="contentSelector">
/// Function to extract content string from payload for warmup detection.
/// </param>
public sealed class TransportTestHarness<TPayload>(
  ITransport transport,
  Func<string, IMessageEnvelope<TPayload>> envelopeFactory,
  Func<TPayload, string> contentSelector
  ) : IAsyncDisposable
  where TPayload : class {
  private readonly ITransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
  private readonly Func<string, IMessageEnvelope<TPayload>> _envelopeFactory = envelopeFactory ?? throw new ArgumentNullException(nameof(envelopeFactory));
  private readonly Func<TPayload, string> _contentSelector = contentSelector ?? throw new ArgumentNullException(nameof(contentSelector));
  private readonly List<IDisposable> _subscriptions = [];

  private string? _currentWarmupId;
  private SignalAwaiter? _warmupAwaiter;
  private MessageAwaiter<IMessageEnvelope>? _testAwaiter;

  /// <summary>
  /// Sets up a subscription with automatic warmup handling.
  /// </summary>
  /// <param name="subscribeDestination">The destination to subscribe to (topic + subscription).</param>
  /// <param name="publishDestination">The destination to publish warmup messages to (topic only).</param>
  /// <param name="warmupTimeout">Maximum time to wait for warmup.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task that completes when the subscription is warmed up and ready.</returns>
  public async Task SetupSubscriptionAsync(
    TransportDestination subscribeDestination,
    TransportDestination publishDestination,
    TimeSpan? warmupTimeout = null,
    CancellationToken cancellationToken = default
  ) {
    _currentWarmupId = SubscriptionWarmup.GenerateWarmupId();

    // Create discriminating awaiters
    (_warmupAwaiter, _testAwaiter, var handler) =
      SubscriptionWarmup.CreateDiscriminatingAwaiters<TPayload>(
        _currentWarmupId,
        _contentSelector
      );

    // Subscribe
    var subscription = await _transport.SubscribeAsync(
      handler,
      subscribeDestination,
      cancellationToken
    );
    _subscriptions.Add(subscription);

    // Warmup
    await SubscriptionWarmup.WarmupAsync(
      _transport,
      publishDestination,
      () => _envelopeFactory(_currentWarmupId),
      _warmupAwaiter,
      warmupTimeout,
      cancellationToken: cancellationToken
    );
  }

  /// <summary>
  /// Publishes a test message and waits for it to be received.
  /// </summary>
  /// <param name="destination">The destination to publish to.</param>
  /// <param name="timeout">Maximum time to wait for the message.</param>
  /// <param name="content">Optional content for the message. Defaults to "test-content".</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The received envelope.</returns>
  public async Task<IMessageEnvelope> PublishAndWaitAsync(
    TransportDestination destination,
    TimeSpan timeout,
    string? content = null,
    CancellationToken cancellationToken = default
  ) {
    if (_testAwaiter == null) {
      throw new InvalidOperationException("Call SetupSubscriptionAsync first.");
    }

    var envelope = _envelopeFactory(content ?? "test-content");
    await _transport.PublishAsync(envelope, destination, cancellationToken: cancellationToken);

    return await _testAwaiter.WaitAsync(timeout, cancellationToken);
  }

  /// <summary>
  /// Gets the test message awaiter for custom assertion patterns.
  /// </summary>
  public MessageAwaiter<IMessageEnvelope>? TestAwaiter => _testAwaiter;

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    foreach (var subscription in _subscriptions) {
      subscription.Dispose();
    }
    _subscriptions.Clear();

    if (_transport is IAsyncDisposable asyncDisposable) {
      await asyncDisposable.DisposeAsync();
    }
  }
}

/// <summary>
/// Factory methods for creating transport test harnesses with common message types.
/// </summary>
public static class TransportTestHarness {
  /// <summary>
  /// Creates a test harness for a simple string-content message type.
  /// </summary>
  /// <typeparam name="TPayload">The payload type with a string content property.</typeparam>
  /// <param name="transport">The transport to test.</param>
  /// <param name="payloadFactory">Factory to create payloads from content strings.</param>
  /// <param name="contentSelector">Selector to extract content from payloads.</param>
  /// <returns>A configured test harness.</returns>
  public static TransportTestHarness<TPayload> Create<TPayload>(
    ITransport transport,
    Func<string, TPayload> payloadFactory,
    Func<TPayload, string> contentSelector
  ) where TPayload : class {
    return new TransportTestHarness<TPayload>(
      transport,
      content => new MessageEnvelope<TPayload> {
        MessageId = MessageId.New(),
        Payload = payloadFactory(content),
        Hops = [
          new MessageHop {
            Type = HopType.Current,
            Timestamp = DateTimeOffset.UtcNow,
            Topic = "test-topic",
            ServiceInstance = ServiceInstanceInfo.Unknown,
            TraceParent = System.Diagnostics.Activity.Current?.Id
          }
        ]
      },
      contentSelector
    );
  }
}
