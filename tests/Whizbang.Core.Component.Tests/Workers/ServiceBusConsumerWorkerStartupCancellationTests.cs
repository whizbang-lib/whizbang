// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

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
  public async Task StoppedBeforeItsStartPoint_CancelsTheReadinessSignalAsync(
      CancellationToken cancellationToken) {
    // A host whose schema step never completes: the worker's start point is never reached, so the
    // host never starts it, and stopping the host is what tells it (#1636).
    var worker = _worker(new StubTransport());
    using var hosted = new Whizbang.Core.Startup.SchemaReadyGatedService<ServiceBusConsumerWorker>(worker, new SchemaReadyGate());
    await hosted.StartAsync(cancellationToken);

    await hosted.StopAsync(CancellationToken.None);

    await Assert.That(async () => await worker.SubscriptionsReady.WaitAsync(cancellationToken))
      .Throws<OperationCanceledException>()
      .Because("a host stopped before the worker started must not leave the signal unsettled — "
             + "during shutdown that is a host that never exits");
    await Assert.That(worker.SubscriptionsReady.IsCanceled).IsTrue()
      .Because("the worker has to settle the signal itself; a wait that only ends on the waiter's "
             + "own timeout is precisely the hang under test, and reads identically from outside");
  }

  [Test]
  [Timeout(30000)]
  public async Task CanceledWhileSubscribing_CancelsTheReadinessSignalAsync(
      CancellationToken cancellationToken) {
    // Past the gate and into subscription setup, which is where a broker that is slow to answer
    // leaves the worker when shutdown arrives.
    var transport = new StubTransport { BlockSubscribeUntilCanceled = true };
    var worker = _worker(transport);

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

  private static ServiceBusConsumerWorker _worker(ITransport transport) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging();
    var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    return new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: scopeFactory,
      logger: new TestLogger<ServiceBusConsumerWorker>(),
      orderedProcessor: new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance, parallelizeStreams: false),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      envelopeSerializer: new EnvelopeSerializer(),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      options: new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("startup-topic", "startup-sub")]
      });
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

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope,
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
