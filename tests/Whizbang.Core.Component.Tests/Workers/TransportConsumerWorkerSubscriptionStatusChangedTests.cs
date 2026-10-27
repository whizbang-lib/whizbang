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

    var disconnect = new InvalidOperationException("link dropped");
    transport.Subscriptions.Single().Disconnect(disconnect);
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
  // Helpers and test doubles
  // ============================================================

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
      ILogger<TransportConsumerWorker>? logger = null) {
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
      controlClass: Options.Create(new ControlClassOptions()));
  }

  private sealed class StatusSubscription : ISubscription {
    public bool IsActive => true;
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() { }

    public void Disconnect(Exception error) =>
      OnDisconnected?.Invoke(this, new SubscriptionDisconnectedEventArgs {
        Reason = "simulated disconnect",
        Exception = error,
        IsApplicationInitiated = false,
      });
  }

  private class StatusTransport(int failFirstSubscribes = 0) : ITransport {
    private int _failuresRemaining = failFirstSubscribes;
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
      var subscription = new StatusSubscription();
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
