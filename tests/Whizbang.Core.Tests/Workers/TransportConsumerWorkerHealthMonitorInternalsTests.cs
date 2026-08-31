using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Health-monitor internals for <see cref="TransportConsumerWorker"/>: the sweep loop's
/// generic exception handler must contain failures raised inside a sweep (here: a logger
/// that throws while reporting the recovery attempt) so the NEXT sweep still recovers the
/// failed subscription. All waits are signal-based (TCS, SubscriptionsReady, OnDisconnected
/// hook) — no test-side polling.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
[Category("Workers")]
public class TransportConsumerWorkerHealthMonitorInternalsTests {

  [Test]
  public async Task HealthMonitor_LoggerThrowsDuringSweep_ErrorIsCaughtAndNextSweepRecoversAsync() {
    // First subscribe attempt throws → state goes Failed (InitialRetryAttempts=1,
    // RetryIndefinitely=false). Sweep 1 of the health monitor logs "attempting to
    // recover" — our saboteur logger throws exactly once on that message, driving the
    // monitor's catch handler. Sweep 2 logs cleanly and recovers the subscription.
    var transport = new MonitorTransport(failFirstBatchSubscribes: 1);
    var logger = new SaboteurLogger();
    var resilience = new SubscriptionResilienceOptions {
      InitialRetryAttempts = 1,
      InitialRetryDelay = TimeSpan.FromMilliseconds(1),
      RetryIndefinitely = false,
      AllowPartialSubscriptions = true,
      HealthCheckInterval = TimeSpan.FromMilliseconds(1),
    };
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("monitor-topic"));
    var worker = new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: resilience,
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: logger,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    // Sweep 1: the sabotaged Information log throws inside the monitor loop — the
    // catch handler must log the error instead of letting the monitor die.
    await logger.MonitorErrorLogged.Task.WaitAsync(TimeSpan.FromSeconds(10));

    // Sweep 2+: the logger no longer throws → the monitor resets the Failed state and
    // re-subscribes. Wait on the OnDisconnected hook, which the retry helper wires
    // strictly AFTER the state flips to Healthy — a deterministic completion signal.
    var recovered = await transport.FirstSubscriptionCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await recovered.Hooked.Task.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(logger.ThrewOnRecoveryLog).IsTrue()
      .Because("The sabotage must actually have fired inside the monitor sweep — otherwise the test proves nothing about the catch handler.");
    await Assert.That(worker.SubscriptionStates.Values.Single().Status).IsEqualTo(SubscriptionStatus.Healthy)
      .Because("A throwing sweep must not kill the health monitor — a later sweep still recovers the subscription.");
    await Assert.That(transport.SubscribeBatchCallCount).IsGreaterThanOrEqualTo(2)
      .Because("Recovery requires a subscribe attempt after the initial failure.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Test doubles
  // ============================================================

  /// <summary>
  /// Logger that throws exactly once when the health monitor logs its recovery attempt,
  /// and signals when the monitor's catch handler logs the resulting error.
  /// </summary>
  private sealed class SaboteurLogger : ILogger<TransportConsumerWorker> {
    private int _thrown;

    public TaskCompletionSource MonitorErrorLogged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool ThrewOnRecoveryLog => Volatile.Read(ref _thrown) == 1;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      var message = formatter(state, exception);
      if (message.Contains("attempting to recover", StringComparison.Ordinal)
          && Interlocked.CompareExchange(ref _thrown, 1, 0) == 0) {
        throw new InvalidOperationException("simulated logging failure inside health monitor sweep");
      }
      if (logLevel == LogLevel.Error && message.Contains("subscription health monitor", StringComparison.Ordinal)) {
        MonitorErrorLogged.TrySetResult();
      }
    }
  }

  private sealed class MonitorSubscription : ISubscription {
    private EventHandler<SubscriptionDisconnectedEventArgs>? _onDisconnected;

    /// <summary>Completes when the retry helper hooks OnDisconnected — which happens strictly
    /// AFTER the state transitions to Healthy, making it a deterministic "subscription
    /// established" signal.</summary>
    public TaskCompletionSource Hooked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsActive => true;

    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected {
      add {
        _onDisconnected += value;
        Hooked.TrySetResult();
      }
      remove => _onDisconnected -= value;
    }

    public Task PauseAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public void Dispose() {
      // Nothing to release — test double.
    }
  }

  private sealed class MonitorTransport(int failFirstBatchSubscribes) : ITransport {
    private int _failuresRemaining = failFirstBatchSubscribes;
    private int _subscribeBatchCallCount;

    public int SubscribeBatchCallCount => Volatile.Read(ref _subscribeBatchCallCount);

    /// <summary>Completes with the first successfully created subscription — signal-based
    /// wait for a test whose initial subscribe attempt intentionally fails.</summary>
    public TaskCompletionSource<MonitorSubscription> FirstSubscriptionCreated { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _subscribeBatchCallCount);
      if (Interlocked.Decrement(ref _failuresRemaining) >= 0) {
        throw new InvalidOperationException("simulated subscribe failure");
      }
      var sub = new MonitorSubscription();
      FirstSubscriptionCreated.TrySetResult(sub);
      return Task.FromResult<ISubscription>(sub);
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull
      => throw new NotSupportedException();
  }
}
