// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Resilience;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

#pragma warning disable CA1822 // Member does not access instance data (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <see cref="TransportConsumerWorker.SubscriptionStatusChanged"/>: raised once per real status change of a
/// destination's subscription, only after the status and the fields that accompany it are recorded, and never
/// allowed to break a subscription when a handler throws. Every wait is on the exact change asserted (#1830).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
[Category("Workers")]
public class TransportConsumerWorkerSubscriptionStatusChangedTests {
  private static readonly TimeSpan _guard = TimeSpan.FromSeconds(10);

  [Test]
  public async Task SubscriptionStatusChanged_EveryDestinationSubscribes_RaisedPendingToHealthyAfterTheStateIsRecordedAsync() {
    var transport = new StatusTransport();
    var worker = _buildWorker(transport, _destinations("a", "b", "c"), new SubscriptionResilienceOptions());
    using var recorder = new SubscriptionStatusRecorder(worker);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await recorder.WhenDestinationsReachAsync(SubscriptionStatus.Healthy, 3).WaitAsync(_guard);

    var changes = recorder.Changes;
    await Assert.That(changes.Count).IsEqualTo(3)
      .Because("each destination changes status exactly once on a clean subscribe");
    await Assert.That(changes.Select(c => c.Destination.Address).Order()).IsEquivalentTo(["a", "b", "c"]);
    foreach (var change in changes) {
      await Assert.That(change.Sender).IsSameReferenceAs(worker);
      await Assert.That(change.PreviousStatus).IsEqualTo(SubscriptionStatus.Pending);
      await Assert.That(change.Status).IsEqualTo(SubscriptionStatus.Healthy);
      await Assert.That(change.StateStatusAtRaise).IsEqualTo(SubscriptionStatus.Healthy)
        .Because("the change is raised after the status is written, so a handler reading the state sees it");
      await Assert.That(change.HadSubscriptionAtRaise).IsTrue()
        .Because("a Healthy subscription's handle is recorded before the change is raised");
    }
    await Assert.That(worker.SubscriptionStates.Values.All(s => s.Status == SubscriptionStatus.Healthy)).IsTrue();

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SubscriptionStatusChanged_SubscribeKeepsFailing_RaisedRecoveringThenFailedWithTheErrorRecordedAsync() {
    var transport = new StatusTransport(failFirstSubscribes: int.MaxValue);
    var resilience = new SubscriptionResilienceOptions {
      InitialRetryAttempts = 2,
      InitialRetryDelay = TimeSpan.FromMilliseconds(1),
      RetryIndefinitely = false,
      AllowPartialSubscriptions = true,
      HealthCheckInterval = TimeSpan.FromHours(1),
    };
    var worker = _buildWorker(transport, _destinations("doomed"), resilience);
    using var recorder = new SubscriptionStatusRecorder(worker);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await recorder.WhenChangedToAsync(SubscriptionStatus.Failed).WaitAsync(_guard);

    var changes = recorder.Changes;
    await Assert.That(changes.Count).IsEqualTo(2)
      .Because("the second failed attempt leaves the status Recovering, which is no change and raises nothing");
    await Assert.That(changes[0].PreviousStatus).IsEqualTo(SubscriptionStatus.Pending);
    await Assert.That(changes[0].Status).IsEqualTo(SubscriptionStatus.Recovering);
    await Assert.That(changes[0].AttemptCountAtRaise).IsEqualTo(1)
      .Because("the failed attempt is counted before the change is raised");
    await Assert.That(changes[0].LastErrorAtRaise).IsTypeOf<InvalidOperationException>()
      .Because("the failure is recorded before the change is raised");
    await Assert.That(changes[1].PreviousStatus).IsEqualTo(SubscriptionStatus.Recovering);
    await Assert.That(changes[1].Status).IsEqualTo(SubscriptionStatus.Failed);
    await Assert.That(worker.SubscriptionStates.Values.Single().Status).IsEqualTo(SubscriptionStatus.Failed);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SubscriptionStatusChanged_HandlerThrows_IsLoggedAndLaterHandlersAndTheSubscriptionAreUnaffectedAsync() {
    var transport = new StatusTransport();
    var logger = new RecordingLogger();
    var worker = _buildWorker(transport, _destinations("guarded"), new SubscriptionResilienceOptions(), logger);
    worker.SubscriptionStatusChanged += (_, _) => throw new InvalidOperationException("monitoring handler failed");
    using var recorder = new SubscriptionStatusRecorder(worker);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await recorder.WhenChangedToAsync(SubscriptionStatus.Healthy).WaitAsync(_guard);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(_guard);

    await Assert.That(worker.SubscriptionStates.Values.Single().Status).IsEqualTo(SubscriptionStatus.Healthy)
      .Because("a throwing handler must not turn a successful subscribe into a failed one");
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(1)
      .Because("a throwing handler must not be mistaken for a subscribe failure and retried");
    var warning = logger.Entries.Single(e => e.Exception is InvalidOperationException { Message: "monitoring handler failed" });
    await Assert.That(warning.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(warning.Message).Contains("guarded");
    await Assert.That(warning.Message).Contains("Healthy");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SubscriptionStatusChanged_StatusWrittenDirectly_RaisedOnlyWhenTheValueChangesAsync() {
    var worker = _buildWorker(new StatusTransport(), _destinations("direct"), new SubscriptionResilienceOptions());
    using var recorder = new SubscriptionStatusRecorder(worker);
    var state = worker.SubscriptionStates.Values.Single();

    state.Status = SubscriptionStatus.Pending;
    await Assert.That(recorder.Changes.Count).IsEqualTo(0)
      .Because("writing the status a state already has is not a change");

    state.Status = SubscriptionStatus.Recovering;
    await Assert.That(recorder.Changes.Count).IsEqualTo(1);
    await Assert.That(recorder.Changes[0].PreviousStatus).IsEqualTo(SubscriptionStatus.Pending);
    await Assert.That(recorder.Changes[0].Status).IsEqualTo(SubscriptionStatus.Recovering);
    await Assert.That(recorder.Changes[0].Destination.Address).IsEqualTo("direct");
  }

  [Test]
  public async Task SubscriptionStatusChanged_NoHandlerAttached_StatusStillChangesAsync() {
    var worker = _buildWorker(new StatusTransport(), _destinations("quiet"), new SubscriptionResilienceOptions());
    var state = worker.SubscriptionStates.Values.Single();

    state.Status = SubscriptionStatus.Healthy;

    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Healthy);
  }

  [Test]
  public async Task SubscriptionStatusChanged_HandlerDetached_NoLongerRaisedAsync() {
    var worker = _buildWorker(new StatusTransport(), _destinations("detached"), new SubscriptionResilienceOptions());
    var recorder = new SubscriptionStatusRecorder(worker);
    var state = worker.SubscriptionStates.Values.Single();

    recorder.Dispose();
    state.Status = SubscriptionStatus.Healthy;

    await Assert.That(recorder.Changes.Count).IsEqualTo(0);
  }

  [Test]
  public async Task SubscriptionStatusChanged_SubscriptionDisconnects_RaisedRecoveringWithTheDisconnectErrorThenHealthyAsync() {
    var transport = new StatusTransport();
    var resilience = new SubscriptionResilienceOptions {
      InitialRetryDelay = TimeSpan.FromMilliseconds(1),
      HealthCheckInterval = TimeSpan.FromHours(1),
    };
    var worker = _buildWorker(transport, _destinations("flaky-link"), resilience);
    using var recorder = new SubscriptionStatusRecorder(worker);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await recorder.WhenChangedToAsync(SubscriptionStatus.Healthy).WaitAsync(_guard);

    // Wait for the disconnect handler too, so this disconnect is the one the handler hears after the subscription
    // is established (one raised before the handler is attached is covered by the next test).
    var subscription = transport.Subscriptions.Single();
    await subscription.DisconnectHandled.Task.WaitAsync(_guard);
    var disconnect = new InvalidOperationException("link dropped");
    subscription.Disconnect(disconnect);
    await recorder.WhenChangedToAsync(SubscriptionStatus.Healthy, count: 2).WaitAsync(_guard);

    var changes = recorder.Changes;
    await Assert.That(string.Join(",", changes.Select(c => c.Status))).IsEqualTo("Healthy,Recovering,Healthy");
    await Assert.That(changes[1].PreviousStatus).IsEqualTo(SubscriptionStatus.Healthy);
    await Assert.That(changes[1].LastErrorAtRaise).IsSameReferenceAs(disconnect)
      .Because("the disconnect's error is recorded before the change to Recovering is raised");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SubscriptionStatusChanged_SubscriptionDisconnectsBeforeTheHandlerIsAttached_RaisedRecoveringThenHealthyAgainAsync() {
    // The first subscription drops inside the retry helper's attach step, before its handler is added, so the
    // disconnect event has no listener; only the subscription's recorded Disconnection can tell the helper (#1831).
    var disconnect = new InvalidOperationException("dropped before the handler was attached");
    var transport = new StatusTransport(disconnectFirstWhileAttaching: disconnect);
    var resilience = new SubscriptionResilienceOptions {
      InitialRetryDelay = TimeSpan.FromMilliseconds(1),
      HealthCheckInterval = TimeSpan.FromHours(1),
    };
    var worker = _buildWorker(transport, _destinations("dropped-early"), resilience);
    using var recorder = new SubscriptionStatusRecorder(worker);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await recorder.WhenChangedToAsync(SubscriptionStatus.Healthy, count: 2).WaitAsync(_guard);

    var changes = recorder.Changes;
    await Assert.That(string.Join(",", changes.Select(c => c.Status))).IsEqualTo("Healthy,Recovering,Healthy")
      .Because("the missed disconnect is acted on right after the subscription is recorded, and the reconnect succeeds");
    await Assert.That(changes[1].LastErrorAtRaise).IsSameReferenceAs(disconnect);
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(2);
    await Assert.That(worker.SubscriptionStates.Values.Single().Subscription).IsSameReferenceAs(transport.Subscriptions.Last())
      .Because("the state ends on the live, reconnected subscription");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SubscriptionStatusChanged_ConnectionRecovered_RaisedPendingWithAResetStateThenHealthyAsync() {
    var transport = new RecoverableStatusTransport(failFirstSubscribes: 1);
    var resilience = new SubscriptionResilienceOptions {
      InitialRetryDelay = TimeSpan.FromMilliseconds(1),
      HealthCheckInterval = TimeSpan.FromHours(1),
    };
    var worker = _buildWorker(transport, _destinations("recovering"), resilience);
    using var recorder = new SubscriptionStatusRecorder(worker);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await recorder.WhenChangedToAsync(SubscriptionStatus.Healthy).WaitAsync(_guard);
    await Assert.That(worker.SubscriptionStates.Values.Single().AttemptCount).IsEqualTo(1)
      .Because("the precondition: one failed attempt is on the counter that recovery must reset");

    await transport.SimulateRecoveryAsync().WaitAsync(_guard);

    var changes = recorder.Changes;
    await Assert.That(string.Join(",", changes.Select(c => c.Status))).IsEqualTo("Recovering,Healthy,Pending,Healthy");
    await Assert.That(changes[2].PreviousStatus).IsEqualTo(SubscriptionStatus.Healthy);
    await Assert.That(changes[2].AttemptCountAtRaise).IsEqualTo(0)
      .Because("recovery resets the attempt counter before the change to Pending is raised");
    await Assert.That(changes[2].HadSubscriptionAtRaise).IsFalse()
      .Because("recovery drops the old subscription before the change to Pending is raised");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SubscriptionStatusChanged_HealthMonitorRetriesAFailedSubscription_RaisedPendingWithAResetCounterThenHealthyAsync() {
    var transport = new StatusTransport(failFirstSubscribes: 1);
    var resilience = new SubscriptionResilienceOptions {
      InitialRetryAttempts = 1,
      InitialRetryDelay = TimeSpan.FromMilliseconds(1),
      RetryIndefinitely = false,
      AllowPartialSubscriptions = true,
      HealthCheckInterval = TimeSpan.FromMilliseconds(1),
    };
    var worker = _buildWorker(transport, _destinations("monitored"), resilience);
    using var recorder = new SubscriptionStatusRecorder(worker);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await recorder.WhenChangedToAsync(SubscriptionStatus.Healthy).WaitAsync(_guard);

    var changes = recorder.Changes;
    await Assert.That(string.Join(",", changes.Select(c => c.Status))).IsEqualTo("Recovering,Failed,Pending,Healthy");
    await Assert.That(changes[2].PreviousStatus).IsEqualTo(SubscriptionStatus.Failed);
    await Assert.That(changes[2].AttemptCountAtRaise).IsEqualTo(0)
      .Because("the health monitor resets the attempt counter before the change to Pending is raised");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // The subscription a reconnect replaces is disposed exactly once (#1839)
  // ============================================================

  [Test]
  public async Task SubscriptionDisconnects_ReconnectDisposesTheReplacedSubscriptionAndShutdownDoesNotDisposeItAgainAsync() {
    var transport = new StatusTransport();
    var worker = _buildWorker(transport, _destinations("flapping"), _reconnectQuickly());
    using var recorder = new SubscriptionStatusRecorder(worker);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await recorder.WhenChangedToAsync(SubscriptionStatus.Healthy).WaitAsync(_guard);
    var replaced = transport.Subscriptions.Single();
    await replaced.DisconnectHandled.Task.WaitAsync(_guard);

    replaced.Disconnect(new InvalidOperationException("link dropped"));
    await recorder.WhenChangedToAsync(SubscriptionStatus.Healthy, count: 2).WaitAsync(_guard);
    var current = transport.Subscriptions.Last();

    await Assert.That(replaced.DisposeCount).IsEqualTo(1)
      .Because("the reconnect disposes the subscription it replaces before it subscribes again");
    await Assert.That(replaced.HandlerCount).IsEqualTo(0);
    await Assert.That(current.DisposeCount).IsEqualTo(0);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(replaced.DisposeCount).IsEqualTo(1)
      .Because("shutdown disposes the live subscription, never the one the reconnect already disposed");
    await Assert.That(current.DisposeCount).IsEqualTo(1);
  }

  [Test]
  public async Task SubscriptionDisconnects_ConnectionRecoveredAfterTheReconnect_DisposesOnlyTheLiveSubscriptionAsync() {
    var transport = new RecoverableStatusTransport(failFirstSubscribes: 0);
    var worker = _buildWorker(transport, _destinations("recovered-after-flap"), _reconnectQuickly());
    using var recorder = new SubscriptionStatusRecorder(worker);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await recorder.WhenChangedToAsync(SubscriptionStatus.Healthy).WaitAsync(_guard);
    var replaced = transport.Subscriptions.Single();
    await replaced.DisconnectHandled.Task.WaitAsync(_guard);
    replaced.Disconnect(new InvalidOperationException("link dropped"));
    await recorder.WhenChangedToAsync(SubscriptionStatus.Healthy, count: 2).WaitAsync(_guard);
    var reconnected = transport.Subscriptions.Last();

    await transport.SimulateRecoveryAsync().WaitAsync(_guard);

    await Assert.That(replaced.DisposeCount).IsEqualTo(1)
      .Because("connection recovery disposes what the state holds, which is no longer the replaced subscription");
    await Assert.That(reconnected.DisposeCount).IsEqualTo(1);
    await Assert.That(transport.Subscriptions.Count).IsEqualTo(3);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(replaced.DisposeCount).IsEqualTo(1);
    await Assert.That(reconnected.DisposeCount).IsEqualTo(1);
    await Assert.That(transport.Subscriptions.Last().DisposeCount).IsEqualTo(1);
  }

  [Test]
  public async Task SubscriptionDisconnectsRightAfterEveryReconnect_EachReconnectWaitsLongerOnTheWorkersClockAsync() {
    // A destination whose receiving fails right after every successful subscribe (#1840): the worker's reconnects
    // back off across the cycles on the clock the worker was given, instead of reconnecting every initial delay.
    var transport = new StatusTransport();
    var clock = new TimerRecordingClock();
    var resilience = new SubscriptionResilienceOptions {
      InitialRetryDelay = TimeSpan.FromSeconds(1),
      HealthCheckInterval = TimeSpan.FromHours(1),
    };
    var worker = _buildWorker(transport, _destinations("keeps-dropping"), resilience, timeProvider: clock);
    using var recorder = new SubscriptionStatusRecorder(worker);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await recorder.WhenChangedToAsync(SubscriptionStatus.Healthy).WaitAsync(_guard);

    var waits = new List<TimeSpan>();
    for (var cycle = 1; cycle <= 3; cycle++) {
      var subscription = transport.Subscriptions.Last();
      await subscription.DisconnectHandled.Task.WaitAsync(_guard);
      subscription.Disconnect(new InvalidOperationException("receiving failed right after subscribing"));
      // The health monitor arms its one-hour sweep on the same clock; that timer is not a reconnect.
      var wait = await clock.NextTimerAsync(dueTime => dueTime != resilience.HealthCheckInterval).WaitAsync(_guard);
      clock.Advance(wait);
      await recorder.WhenChangedToAsync(SubscriptionStatus.Healthy, count: cycle + 1).WaitAsync(_guard);
      waits.Add(wait);
    }

    await Assert.That(string.Join(",", waits.Select(w => w.TotalSeconds))).IsEqualTo("1,2,4")
      .Because("each reconnect waits the previous reconnect's delay times the multiplier, on the worker's clock");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Helpers and test doubles
  // ============================================================

  private static SubscriptionResilienceOptions _reconnectQuickly() => new() {
    InitialRetryDelay = TimeSpan.Zero,
    HealthCheckInterval = TimeSpan.FromHours(1),
  };

  private static TransportConsumerOptions _destinations(params string[] addresses) {
    var options = new TransportConsumerOptions();
    foreach (var address in addresses) {
      options.Destinations.Add(new TransportDestination(address));
    }
    return options;
  }

  private static TransportConsumerWorker _buildWorker(
      ITransport transport,
      TransportConsumerOptions options,
      SubscriptionResilienceOptions resilience,
      ILogger<TransportConsumerWorker>? logger = null,
      TimeProvider? timeProvider = null) {
    var serviceProvider = new ServiceCollection().BuildServiceProvider();
    return new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: resilience,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance, parallelizeStreams: false),
      metrics: null,
      logger: logger ?? NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      routingOptions: Options.Create(new RoutingOptions()),
      workChannelWriter: new WorkChannelWriter(),
      claimWorkerOptions: Options.Create(new ClaimWorkerOptions()),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      controlClass: Options.Create(new ControlClassOptions()),
      timeProvider: timeProvider);
  }

  private sealed class StatusSubscription(Exception? disconnectWhileAttaching = null) : ISubscription {
    private EventHandler<SubscriptionDisconnectedEventArgs>? _onDisconnected;
    private int _disposeCount;

    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public int HandlerCount => _onDisconnected?.GetInvocationList().Length ?? 0;

    /// <summary>Completes when a disconnect handler is attached: the precondition for a disconnect to be handled.</summary>
    public TaskCompletionSource DisconnectHandled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsActive => true;

    /// <summary>The unexpected disconnect, recorded before it is raised, as a transport records it.</summary>
    public SubscriptionDisconnectedEventArgs? Disconnection { get; private set; }

    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected {
      add {
        // Raised before the handler is added, so this disconnect has no listener.
        if (disconnectWhileAttaching is not null) {
          Disconnect(disconnectWhileAttaching);
        }
        _onDisconnected += value;
        DisconnectHandled.TrySetResult();
      }
      remove => _onDisconnected -= value;
    }
    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() => Interlocked.Increment(ref _disposeCount);

    public void Disconnect(Exception error) {
      var args = new SubscriptionDisconnectedEventArgs {
        Reason = "simulated disconnect",
        Exception = error,
        IsApplicationInitiated = false,
      };
      Disconnection ??= args;
      _onDisconnected?.Invoke(this, args);
    }
  }

  private class StatusTransport(int failFirstSubscribes = 0, Exception? disconnectFirstWhileAttaching = null) : ITransport {
    private int _failuresRemaining = failFirstSubscribes;
    private Exception? _disconnectFirstWhileAttaching = disconnectFirstWhileAttaching;
    private int _subscribeCallCount;

    public int SubscribeCallCount => _subscribeCallCount;
    public ConcurrentQueue<StatusSubscription> Subscriptions { get; } = new();
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _subscribeCallCount);
      if (Interlocked.Decrement(ref _failuresRemaining) >= 0) {
        throw new InvalidOperationException("simulated subscribe failure");
      }
      var subscription = new StatusSubscription(Interlocked.Exchange(ref _disconnectFirstWhileAttaching, null));
      Subscriptions.Enqueue(subscription);
      return Task.FromResult<ISubscription>(subscription);
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull
      => throw new NotSupportedException();
  }

  private sealed class RecoverableStatusTransport(int failFirstSubscribes) : StatusTransport(failFirstSubscribes), ITransportWithRecovery {
    private Func<CancellationToken, Task>? _onRecovered;
    public void SetRecoveryHandler(Func<CancellationToken, Task>? onRecovered) => _onRecovered = onRecovered;
    public Task SimulateRecoveryAsync() => _onRecovered!(CancellationToken.None);
  }

  private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

  private sealed class RecordingLogger : ILogger<TransportConsumerWorker> {
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    public IReadOnlyList<LogEntry> Entries => [.. _entries];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
  }
}
