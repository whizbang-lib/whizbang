// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Resilience;

/// <summary>
/// A transport disconnect raised while <see cref="SubscriptionRetryHelper"/> is still recording a new subscription
/// (#1831). Whenever the disconnect lands, the destination must end Recovering with a reconnect under way, never
/// Healthy with no live subscription; and a disconnect from a subscription the state no longer holds, or a second one
/// from the same subscription, must not start another reconnect.
/// </summary>
/// <remarks>
/// The test subscription raises its disconnect from inside the helper's own attach step (before or after the handler
/// is added), so the disconnect lands at exactly that point by construction, on every run. A reconnect parks on a gate
/// the test never opens, so the status the helper left cannot be overwritten before it is read; the test cancels the
/// helper's token at the end, which unwinds the parked reconnect.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Resilience/SubscriptionRetryHelper.cs</code-under-test>
[Category("Resilience")]
public class SubscriptionRetryHelperDisconnectWindowTests {
  private static readonly TimeSpan _guard = TimeSpan.FromSeconds(10);

  [Test]
  public async Task SubscribeWithRetryAsync_DisconnectBeforeTheHandlerIsAttached_EndsRecoveringAndReconnectsAsync() {
    var error = new InvalidOperationException("dropped before the handler was attached");
    var transport = new WindowTransport(first => first.Attaching = s => s.Disconnect(error));
    var state = new SubscriptionState(transport.Destination);
    var logger = new RecordingLogger();
    using var cts = new CancellationTokenSource();

    await transport.SubscribeAsync(state, logger, cts.Token);

    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Recovering)
      .Because("a disconnect raised before the handler is attached has no listener; the helper must still find it");
    await Assert.That(state.LastError).IsSameReferenceAs(error);
    await transport.ReconnectRequested.Task.WaitAsync(_guard);
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(2)
      .Because("the missed disconnect starts exactly one reconnect");
    await Assert.That(logger.DisconnectWarnings).IsEqualTo(1);

    await cts.CancelAsync();
  }

  [Test]
  public async Task SubscribeWithRetryAsync_DisconnectAfterTheHandlerIsAttachedBeforeHealthyIsRecorded_IsNotOverwrittenByHealthyAsync() {
    var error = new InvalidOperationException("dropped while the subscription was being recorded");
    // Raised to the handler only, as by a transport that keeps no record of its disconnect: this isolates the
    // ordering of the handler's Recovering against the helper's Healthy.
    var transport = new WindowTransport(first => first.Attached = s => s.Disconnect(error, record: false));
    var state = new SubscriptionState(transport.Destination);
    var logger = new RecordingLogger();
    using var cts = new CancellationTokenSource();

    await transport.SubscribeAsync(state, logger, cts.Token);

    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Recovering)
      .Because("the change to Healthy recorded after the disconnect was heard must not overwrite Recovering");
    await Assert.That(state.LastError).IsSameReferenceAs(error);
    await transport.ReconnectRequested.Task.WaitAsync(_guard);
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(2);
    await Assert.That(logger.DisconnectWarnings).IsEqualTo(1);

    await cts.CancelAsync();
  }

  [Test]
  public async Task SubscribeWithRetryAsync_DisconnectBothRecordedAndRaisedInTheWindow_ReconnectsOnceAsync() {
    var error = new InvalidOperationException("dropped and recorded while the handler was being attached");
    var transport = new WindowTransport(first => first.Attached = s => s.Disconnect(error));
    var state = new SubscriptionState(transport.Destination);
    var logger = new RecordingLogger();
    using var cts = new CancellationTokenSource();

    await transport.SubscribeAsync(state, logger, cts.Token);

    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Recovering);
    await Assert.That(logger.DisconnectWarnings).IsEqualTo(1)
      .Because("the handler heard the disconnect and the subscription also recorded it; it is one disconnect");
    await transport.ReconnectRequested.Task.WaitAsync(_guard);

    await cts.CancelAsync();
  }

  [Test]
  public async Task SubscribeWithRetryAsync_ApplicationInitiatedDisconnectInTheWindow_StaysHealthyAsync() {
    var transport = new WindowTransport(first => first.Attaching = s => s.Disconnect(error: null, applicationInitiated: true));
    var state = new SubscriptionState(transport.Destination);
    var logger = new RecordingLogger();
    using var cts = new CancellationTokenSource();

    await transport.SubscribeAsync(state, logger, cts.Token);

    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Healthy)
      .Because("a disconnect the application asked for is a shutdown, not a fault to recover from");
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(1);
    await Assert.That(logger.DisconnectWarnings).IsEqualTo(0);

    await cts.CancelAsync();
  }

  [Test]
  public async Task SubscribeWithRetryAsync_DisconnectOfASubscriptionTheStateNoLongerHolds_LeavesTheStateAloneAsync() {
    var transport = new WindowTransport();
    var state = new SubscriptionState(transport.Destination);
    var logger = new RecordingLogger();
    using var cts = new CancellationTokenSource();
    await transport.SubscribeAsync(state, logger, cts.Token);
    var replaced = transport.Subscriptions.Single();
    // A later subscribe for the same destination has recorded its own, newer subscription.
    state.Subscription = new WindowSubscription();

    replaced.Disconnect(new InvalidOperationException("the old link finally dropped"));

    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Healthy)
      .Because("a disconnect of a replaced subscription says nothing about the one the state now holds");
    await Assert.That(state.LastError).IsNull();
    await Assert.That(logger.DisconnectWarnings).IsEqualTo(0);
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(1)
      .Because("the decision not to reconnect is made before the handler returns");

    await cts.CancelAsync();
  }

  [Test]
  public async Task SubscribeWithRetryAsync_SubscriptionDisconnectsTwice_ReconnectsOnceAsync() {
    var transport = new WindowTransport();
    var state = new SubscriptionState(transport.Destination);
    var logger = new RecordingLogger();
    using var cts = new CancellationTokenSource();
    await transport.SubscribeAsync(state, logger, cts.Token);
    var subscription = transport.Subscriptions.Single();

    subscription.Disconnect(new InvalidOperationException("first report"));
    subscription.Disconnect(new InvalidOperationException("second report of the same drop"));

    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Recovering);
    await Assert.That(state.LastError!.Message).IsEqualTo("first report");
    await Assert.That(logger.DisconnectWarnings).IsEqualTo(1)
      .Because("one subscription is replaced once; a second reconnect would leave two consumers on the destination");
    await transport.ReconnectRequested.Task.WaitAsync(_guard);

    await cts.CancelAsync();
  }

  // ============================================================
  // Test doubles
  // ============================================================

  /// <summary>
  /// Returns a <see cref="WindowSubscription"/> per subscribe. Every subscribe after the first is a reconnect: it
  /// signals <see cref="ReconnectRequested"/> and then waits for cancellation, so it never records a status.
  /// </summary>
  private sealed class WindowTransport(Action<WindowSubscription>? configureFirst = null) : ITransport {
    private int _subscribeCallCount;

    public TransportDestination Destination { get; } = new("window-topic", "window-key");
    public int SubscribeCallCount => Volatile.Read(ref _subscribeCallCount);
    public ConcurrentQueue<WindowSubscription> Subscriptions { get; } = new();
    public TaskCompletionSource ReconnectRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task SubscribeAsync(SubscriptionState state, ILogger logger, CancellationToken cancellationToken) =>
      SubscriptionRetryHelper.SubscribeWithRetryAsync(
        this, Destination, (_, _) => Task.CompletedTask, new TransportBatchOptions(), state,
        new SubscriptionResilienceOptions { InitialRetryDelay = TimeSpan.Zero }, logger, cancellationToken);

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      if (Interlocked.Increment(ref _subscribeCallCount) > 1) {
        ReconnectRequested.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
      }
      var subscription = new WindowSubscription();
      configureFirst?.Invoke(subscription);
      Subscriptions.Enqueue(subscription);
      return subscription;
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull
      => throw new NotSupportedException();
  }

  /// <summary>
  /// A subscription whose attach step runs <see cref="Attaching"/> before the handler is added and
  /// <see cref="Attached"/> after, and which records its unexpected disconnect in <see cref="Disconnection"/> before
  /// raising it, as a transport does.
  /// </summary>
  private sealed class WindowSubscription : ISubscription {
    private EventHandler<SubscriptionDisconnectedEventArgs>? _onDisconnected;

    public Action<WindowSubscription>? Attaching { get; set; }
    public Action<WindowSubscription>? Attached { get; set; }
    public SubscriptionDisconnectedEventArgs? Disconnection { get; private set; }
    public bool IsActive => true;

    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected {
      add {
        Attaching?.Invoke(this);
        _onDisconnected += value;
        Attached?.Invoke(this);
      }
      remove => _onDisconnected -= value;
    }

    public void Disconnect(Exception? error, bool applicationInitiated = false, bool record = true) {
      var args = new SubscriptionDisconnectedEventArgs {
        Reason = "simulated disconnect",
        Exception = error,
        IsApplicationInitiated = applicationInitiated,
      };
      if (record && !applicationInitiated) {
        Disconnection ??= args;
      }
      _onDisconnected?.Invoke(this, args);
    }

    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() { }
  }

  private sealed class RecordingLogger : ILogger {
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    /// <summary>Warnings the helper logs when it accepts a disconnect and starts a reconnect.</summary>
    public int DisconnectWarnings => _entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("disconnected", StringComparison.Ordinal));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
      _entries.Enqueue((logLevel, formatter(state, exception)));
  }
}
