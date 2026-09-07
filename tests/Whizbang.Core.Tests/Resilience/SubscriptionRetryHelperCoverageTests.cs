using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Resilience;

/// <summary>
/// Coverage for two of <see cref="SubscriptionRetryHelper"/>'s failure edges: a subscribe attempt
/// that fails with cancellation rather than a transient fault, and a reconnection attempt that
/// fails before it can even retry. A retry helper's real job is deciding what is worth
/// retrying — retrying a permanent (or shutdown-driven) failure loops forever, and giving up on a
/// transient one turns a blip into an outage — so both tests are about that classification, not
/// the retrying mechanics themselves.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Resilience/SubscriptionRetryHelper.cs</code-under-test>
public class SubscriptionRetryHelperCoverageTests {

  private static Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> _noOpHandler() =>
    (_, _) => Task.CompletedTask;

  // If a subscribe attempt that fails because the caller is shutting down were treated like any
  // other transient fault, shutdown would sit in a retry backoff trying to re-subscribe to a
  // transport that is going away, instead of unwinding immediately.
  [Test]
  public async Task SubscribeWithRetryAsync_TransportThrowsOperationCanceled_PropagatesWithoutRetryingAsync() {
    var transport = new _throwingTransport(new OperationCanceledException("simulated shutdown"));
    var destination = new TransportDestination("coverage-topic");
    var state = new SubscriptionState(destination);
    var options = new SubscriptionResilienceOptions {
      InitialRetryAttempts = 5,
      InitialRetryDelay = TimeSpan.FromMilliseconds(1),
      RetryIndefinitely = false
    };

    await Assert.That(() => SubscriptionRetryHelper.SubscribeWithRetryAsync(
        transport, destination, _noOpHandler(), new TransportBatchOptions(), state, options,
        NullLogger.Instance, CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("cancellation must propagate immediately out of the catch that rethrows it, not fall into the retry catch below it");

    await Assert.That(transport.SubscribeCallCount).IsEqualTo(1)
      .Because("a canceled attempt must not be retried — exactly one call means the catch rethrew instead of looping back to subscribe again");
  }

  // If a reconnection attempt's own failure were swallowed instead of logged, an operator watching
  // this consumer would see it go quiet after a disconnect with no record that the automatic
  // reconnection itself blew up — this is the one place that failure is otherwise visible at all.
  [Test]
  public async Task SubscribeWithRetryAsync_ReconnectionAttemptFailsBeforeRetrying_LogsReconnectionFailedAsync() {
    var transport = new _disconnectableTransport();
    var destination = new TransportDestination("coverage-topic", "coverage-routing");
    var state = new SubscriptionState(destination);
    var logger = new _signalingLogger();
    // An invalid negative delay (anything but Timeout.InfiniteTimeSpan) makes the reconnection's
    // own Task.Delay throw ArgumentOutOfRangeException before it ever calls SubscribeBatchAsync
    // again — a deterministic way to reach the reconnection's own catch(Exception) without racing
    // a real clock or a flaky transport.
    var options = new SubscriptionResilienceOptions {
      InitialRetryDelay = TimeSpan.FromMilliseconds(-5)
    };

    await SubscriptionRetryHelper.SubscribeWithRetryAsync(
      transport, destination, _noOpHandler(), new TransportBatchOptions(), state, options,
      logger, CancellationToken.None);

    var subscription = (_disconnectableSubscription)state.Subscription!;
    subscription.TriggerDisconnect(applicationInitiated: false);

    await logger.ErrorLogged.WaitAsync(TimeSpan.FromSeconds(10));

    var entry = logger.Entries.Single(e => e.Level == LogLevel.Error);
    await Assert.That(entry.Message).Contains(destination.Address);
  }

  private sealed class _throwingTransport(Exception exceptionToThrow) : ITransport {
    public int SubscribeCallCount { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope,
        TransportDestination destination,
        string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      throw exceptionToThrow;
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();
  }

  private sealed class _disconnectableTransport : ITransport {
    public int SubscribeCallCount { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope,
        TransportDestination destination,
        string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      return Task.FromResult<ISubscription>(new _disconnectableSubscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();
  }

  private sealed class _disconnectableSubscription : ISubscription {
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
    public bool IsActive { get; private set; } = true;
    public Task PauseAsync() { IsActive = false; return Task.CompletedTask; }
    public Task ResumeAsync() { IsActive = true; return Task.CompletedTask; }
    public void Dispose() => IsActive = false;

    public void TriggerDisconnect(bool applicationInitiated) =>
      OnDisconnected?.Invoke(this, new SubscriptionDisconnectedEventArgs {
        Reason = "coverage-triggered disconnect",
        IsApplicationInitiated = applicationInitiated
      });
  }

  // Signals on the first Error-level log rather than any log call: the initial successful
  // subscribe already logs at Debug, and that must not be mistaken for the reconnection failure
  // this test is waiting on.
  private sealed class _signalingLogger : ILogger {
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public Task ErrorLogged => _tcs.Task;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) {
      Entries.Add((logLevel, formatter(state, exception)));
      if (logLevel == LogLevel.Error) {
        _tcs.TrySetResult();
      }
    }
  }
}
