using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// What happens to <see cref="ServiceBusConsumerWorker.SubscriptionsReady"/> when the host shuts
/// down before the worker finishes starting.
/// <para>
/// The readiness signal exists so other components can wait until this worker is actually
/// consuming — <c>WaitForSubscriptionsAsync</c> is the wait. A worker that returns from startup
/// without settling that signal leaves every waiter parked forever, which during shutdown is a
/// host that never exits. Both places startup can be cut short have to cancel it, and neither was
/// exercised: they only run when cancellation lands inside a specific window.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ServiceBusConsumerWorker.cs</code-under-test>
public class ServiceBusConsumerWorkerStartupCancellationTests {

  [Test]
  [Timeout(30000)]
  public async Task CanceledWhileWaitingForTheSchemaGate_CancelsTheReadinessSignalAsync(
      CancellationToken cancellationToken) {
    // A host whose schema step never completes: the gate is never marked ready, so the worker is
    // still parked on it when shutdown arrives.
    var neverReady = new SignallingSchemaGate();
    var worker = _worker(new StubTransport(), neverReady);

    using var stopping = new CancellationTokenSource();
    await worker.StartAsync(stopping.Token);

    // Park on the gate first, exactly as the sibling test waits for SubscribeEntered. StartAsync
    // only queues ExecuteAsync via Task.Run(_, stoppingToken), and Task.Run skips the delegate
    // entirely when the token is already canceled at dequeue time — so canceling straight after
    // StartAsync could leave _subscriptionsReady untouched. This assertion still "passed" then,
    // because a signal nobody ever settles throws OperationCanceledException off the test's own
    // timeout token instead of off the worker's TrySetCanceled — the opposite of what it claims
    // to prove, and only after burning the whole timeout.
    await neverReady.Entered.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    await stopping.CancelAsync();

    await Assert.That(async () => await worker.SubscriptionsReady.WaitAsync(cancellationToken))
      .Throws<OperationCanceledException>()
      .Because("returning from startup without settling the signal parks every waiter forever — "
             + "during shutdown that is a host that never exits");
    await Assert.That(worker.SubscriptionsReady.IsCanceled).IsTrue()
      .Because("the worker has to settle the signal itself; a wait that only ends on the waiter's "
             + "own timeout is precisely the hang under test, and reads identically from outside");
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  [Timeout(30000)]
  public async Task CanceledWhileSubscribing_CancelsTheReadinessSignalAsync(
      CancellationToken cancellationToken) {
    // Past the gate and into subscription setup, which is where a broker that is slow to answer
    // leaves the worker when shutdown arrives.
    var transport = new StubTransport { BlockSubscribeUntilCanceled = true };
    var worker = _worker(transport, SchemaReadyGate.AlreadyReady());

    using var stopping = new CancellationTokenSource();
    await worker.StartAsync(stopping.Token);
    await transport.SubscribeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    await stopping.CancelAsync();

    await Assert.That(async () => await worker.SubscriptionsReady.WaitAsync(cancellationToken))
      .Throws<OperationCanceledException>()
      .Because("a subscribe cut short by shutdown must settle the signal too — the waiter cannot "
             + "tell which stage the worker was in");
    await worker.StopAsync(CancellationToken.None);
  }

  private static ServiceBusConsumerWorker _worker(ITransport transport, ISchemaReadyGate gate) {
    var services = new ServiceCollection();
    services.AddLogging();
    var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    return new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      logger: new TestLogger<ServiceBusConsumerWorker>(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      options: new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("startup-topic", "startup-sub")]
      },
      schemaReadyGate: gate);
  }

  /// <summary>
  /// A never-ready schema gate that publishes when a waiter arrives.
  /// </summary>
  /// <remarks>
  /// The gate-stage counterpart of <see cref="StubTransport.SubscribeEntered"/>: it marks the
  /// moment the worker is provably inside the window the first test is about, so shutdown can be
  /// aimed at that window instead of at whenever the thread pool got around to the body.
  /// </remarks>
  private sealed class SignallingSchemaGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes the moment the worker begins waiting on this gate.</summary>
    public Task Entered => _entered.Task;

    public bool IsReady => _ready.Task.IsCompleted;
    public void MarkReady() => _ready.TrySetResult();

    public Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _entered.TrySetResult();
      return _ready.Task.WaitAsync(cancellationToken);
    }
  }

  /// <summary>
  /// Subscribes instantly by default; with <see cref="BlockSubscribeUntilCanceled"/> it parks
  /// inside the subscribe call, which is the window the second test needs.
  /// </summary>
  private sealed class StubTransport : ITransport {
    public bool BlockSubscribeUntilCanceled { get; init; }
    public TaskCompletionSource SubscribeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination,
        CancellationToken cancellationToken = default) {
      SubscribeEntered.TrySetResult();
      if (BlockSubscribeUntilCanceled) {
        await Task.Delay(Timeout.Infinite, cancellationToken);
      }
      return new StubSubscription();
    }

    public async Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      SubscribeEntered.TrySetResult();
      if (BlockSubscribeUntilCanceled) {
        await Task.Delay(Timeout.Infinite, cancellationToken);
      }
      return new StubSubscription();
    }

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope envelope,
        TransportDestination destination, CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();
  }

  private sealed class StubSubscription : ISubscription {
    public bool IsActive { get; private set; } = true;
#pragma warning disable CS0067 // Required by the interface; nothing raises it in this double
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067
    public Task PauseAsync() { IsActive = false; return Task.CompletedTask; }
    public Task ResumeAsync() { IsActive = true; return Task.CompletedTask; }
    public void Dispose() => IsActive = false;
  }
}
