using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)
#pragma warning disable CA1822 // Member does not access instance data (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Resilience edge-path coverage for <see cref="TransportConsumerWorker"/>:
/// <list type="bullet">
/// <item><description>Readiness check returning false (no subscriptions, readiness waiters released)</description></item>
/// <item><description>Owned-domain infrastructure provisioning before subscribe (success + throw)</description></item>
/// <item><description>AllowPartialSubscriptions=false failing startup and faulting <c>SubscriptionsReady</c></description></item>
/// <item><description>Health monitor recovering a Failed subscription</description></item>
/// <item><description>Connection-recovered handler resetting state and re-subscribing</description></item>
/// <item><description>PauseAllSubscriptionsAsync / ResumeAllSubscriptionsAsync</description></item>
/// <item><description>Empty transport batch early-return</description></item>
/// <item><description>Hierarchical owned-namespace matching + self-echo discard (incl. the zero-hops branch)</description></item>
/// </list>
/// All waits are signal-based (TCS/SemaphoreSlim, SubscriptionsReady) — no test-side polling.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
[Category("Workers")]
public class TransportConsumerWorkerResilienceEdgeTests {

  // ============================================================
  // Test doubles
  // ============================================================

  private sealed class EdgeSubscription : ISubscription {
    private EventHandler<SubscriptionDisconnectedEventArgs>? _onDisconnected;
    /// <summary>Completes when the retry helper hooks OnDisconnected — which happens strictly
    /// AFTER the state transitions to Healthy, making it a deterministic "subscription
    /// established" signal.</summary>
    public TaskCompletionSource Hooked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsActive { get; private set; } = true;
    public bool IsDisposed { get; private set; }
    public int PauseCalls { get; private set; }
    public int ResumeCalls { get; private set; }

    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected {
      add {
        _onDisconnected += value;
        Hooked.TrySetResult();
      }
      remove => _onDisconnected -= value;
    }

    public Task PauseAsync() {
      PauseCalls++;
      IsActive = false;
      return Task.CompletedTask;
    }

    public Task ResumeAsync() {
      ResumeCalls++;
      IsActive = true;
      return Task.CompletedTask;
    }

    public void Dispose() => IsDisposed = true;
  }

  private class EdgeTransport(int failFirstBatchSubscribes = 0) : ITransport {
    private int _failuresRemaining = failFirstBatchSubscribes;
    private int _subscribeBatchCallCount;
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _batchHandler;

    public int SubscribeBatchCallCount => _subscribeBatchCallCount;
    public ConcurrentQueue<EdgeSubscription> Subscriptions { get; } = new();
    /// <summary>Completes with the first successfully created subscription — signal-based
    /// wait for tests whose initial subscribe attempts intentionally fail.</summary>
    public TaskCompletionSource<EdgeSubscription> FirstSubscriptionCreated { get; } =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
      => throw new NotSupportedException("Batch subscription is the exercised path");

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _subscribeBatchCallCount);
      if (Interlocked.Decrement(ref _failuresRemaining) >= 0) {
        throw new InvalidOperationException("simulated subscribe failure");
      }
      _batchHandler = batchHandler;
      var sub = new EdgeSubscription();
      Subscriptions.Enqueue(sub);
      FirstSubscriptionCreated.TrySetResult(sub);
      return Task.FromResult<ISubscription>(sub);
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull
      => throw new NotSupportedException();

    public Task DeliverBatchAsync(IReadOnlyList<TransportMessage> messages)
      => _batchHandler is null
        ? throw new InvalidOperationException("No batch handler subscribed yet")
        : _batchHandler(messages, CancellationToken.None);
  }

  private sealed class RecoverableEdgeTransport : EdgeTransport, ITransportWithRecovery {
    private Func<CancellationToken, Task>? _onRecovered;
    public bool RecoveryHandlerRegistered => _onRecovered is not null;
    public void SetRecoveryHandler(Func<CancellationToken, Task>? onRecovered) => _onRecovered = onRecovered;
    public Task SimulateRecoveryAsync(CancellationToken ct) =>
      _onRecovered?.Invoke(ct) ?? Task.CompletedTask;
  }

  private sealed class FalseReadinessCheck : ITransportReadinessCheck {
    public int Calls { get; private set; }
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      Calls++;
      return Task.FromResult(false);
    }
  }

  private sealed class RecordingProvisioner(Func<int> subscribeCountReader, Exception? throwOnProvision = null) : IInfrastructureProvisioner {
    public IReadOnlySet<string>? ProvisionedDomains { get; private set; }
    public int SubscribeCountAtProvisionTime { get; private set; } = -1;
    public Task ProvisionOwnedDomainsAsync(IReadOnlySet<string> ownedDomains, CancellationToken cancellationToken = default) {
      ProvisionedDomains = ownedDomains;
      SubscribeCountAtProvisionTime = subscribeCountReader();
      if (throwOnProvision is not null) {
        throw throwOnProvision;
      }
      return Task.CompletedTask;
    }
  }

  private sealed class EdgeInstanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => serviceName;
    public string HostName => "edge-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static TransportConsumerWorker _buildWorker(
      ITransport transport,
      TransportConsumerOptions options,
      SubscriptionResilienceOptions resilience,
      IServiceProvider serviceProvider,
      IOptions<RoutingOptions>? routingOptions = null,
      IServiceInstanceProvider? instanceProvider = null) {
    return new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: resilience,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      routingOptions: routingOptions,
      serviceInstanceProvider: instanceProvider ?? new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());
  }

  private static TransportConsumerOptions _oneDestination(string address = "edge-topic") {
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination(address));
    return options;
  }

  private static MessageEnvelope<JsonElement> _envelope(MessageId messageId, string? echoServiceName = null) {
    List<MessageHop> hops = echoServiceName is null
      ? []
      : [
          new MessageHop {
            Type = HopType.Current,
            Timestamp = DateTimeOffset.UtcNow,
            ServiceInstance = new ServiceInstanceInfo {
              InstanceId = Guid.CreateVersion7(),
              ServiceName = echoServiceName,
              HostName = "other-host",
              ProcessId = 99,
            },
          }
        ];
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = hops,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
  }

  // ============================================================
  // Readiness check
  // ============================================================

  [Test]
  public async Task ExecuteAsync_ReadinessCheckFalse_ReleasesReadinessWaitersWithoutSubscribingAsync() {
    var transport = new EdgeTransport();
    var readiness = new FalseReadinessCheck();
    var services = new ServiceCollection();
    services.AddSingleton<ITransportReadinessCheck>(readiness);
    var sp = services.BuildServiceProvider();

    var worker = _buildWorker(transport, _oneDestination(), new SubscriptionResilienceOptions(), sp);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Readiness waiters MUST be released even though no subscription will ever be issued —
    // otherwise probes awaiting SubscriptionsReady hang forever.
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(readiness.Calls).IsEqualTo(1)
      .Because("The configured readiness check gates subscription setup.");
    await Assert.That(transport.SubscribeBatchCallCount).IsEqualTo(0)
      .Because("A not-ready transport must receive zero subscription attempts.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Owned-domain provisioning
  // ============================================================

  [Test]
  public async Task ExecuteAsync_WithProvisionerAndOwnedDomains_ProvisionsBeforeSubscribingAsync() {
    var transport = new EdgeTransport();
    var provisioner = new RecordingProvisioner(() => transport.SubscribeBatchCallCount);
    var routing = new RoutingOptions();
    routing.OwnDomains("TestApp.Orders");

    var services = new ServiceCollection();
    services.AddSingleton<IInfrastructureProvisioner>(provisioner);
    services.AddSingleton<IOptions<RoutingOptions>>(Options.Create(routing));
    var sp = services.BuildServiceProvider();

    var worker = _buildWorker(transport, _oneDestination(), new SubscriptionResilienceOptions(), sp,
      routingOptions: Options.Create(routing));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(provisioner.ProvisionedDomains).IsNotNull()
      .Because("Owned domains + a registered provisioner must trigger infrastructure provisioning at startup.");
    await Assert.That(provisioner.ProvisionedDomains!).Contains("TestApp.Orders")
      .Because("The provisioner must receive the exact owned-domain set from RoutingOptions.");
    await Assert.That(provisioner.SubscribeCountAtProvisionTime).IsEqualTo(0)
      .Because("Provisioning must complete BEFORE any subscription is created — subscribing to a topic that doesn't exist yet fails.");
    await Assert.That(transport.SubscribeBatchCallCount).IsEqualTo(1)
      .Because("After provisioning, normal subscription setup proceeds.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_ProvisionerThrows_FaultsSubscriptionsReadyAsync() {
    var transport = new EdgeTransport();
    var provisioner = new RecordingProvisioner(
      () => transport.SubscribeBatchCallCount,
      throwOnProvision: new InvalidOperationException("simulated provisioning failure"));
    var routing = new RoutingOptions();
    routing.OwnDomains("TestApp.Orders");

    var services = new ServiceCollection();
    services.AddSingleton<IInfrastructureProvisioner>(provisioner);
    services.AddSingleton<IOptions<RoutingOptions>>(Options.Create(routing));
    var sp = services.BuildServiceProvider();

    var worker = _buildWorker(transport, _oneDestination(), new SubscriptionResilienceOptions(), sp,
      routingOptions: Options.Create(routing));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Startup failure must surface to readiness waiters — hanging forever would mask the broken deploy.
    var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
      async () => await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    await Assert.That(thrown!.Message).Contains("simulated provisioning failure")
      .Because("The ORIGINAL provisioning exception must reach readiness waiters for diagnosability.");
    await Assert.That(transport.SubscribeBatchCallCount).IsEqualTo(0)
      .Because("Provisioning failed before any subscription attempt.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // AllowPartialSubscriptions = false
  // ============================================================

  [Test]
  public async Task ExecuteAsync_AllowPartialFalse_SubscriptionFails_FaultsSubscriptionsReadyAsync() {
    // InitialRetryAttempts=0 + RetryIndefinitely=false → the retry helper gives up
    // immediately (zero transport calls) and marks the state Failed — no timers involved.
    var transport = new EdgeTransport(failFirstBatchSubscribes: int.MaxValue);
    var resilience = new SubscriptionResilienceOptions {
      InitialRetryAttempts = 0,
      RetryIndefinitely = false,
      AllowPartialSubscriptions = false,
    };
    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = _buildWorker(transport, _oneDestination(), resilience, sp);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // AllowPartialSubscriptions=false turns any Failed subscription into a startup failure
    // that readiness waiters must observe.
    var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
      async () => await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    await Assert.That(thrown!.Message).Contains("AllowPartialSubscriptions=false")
      .Because("The failure message must name the config knob so operators know how to relax the gate.");
    await Assert.That(worker.SubscriptionStates.Values.Single().Status).IsEqualTo(SubscriptionStatus.Failed)
      .Because("The destination's state must reflect the permanent failure.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Health monitor recovery
  // ============================================================

  [Test]
  public async Task HealthMonitor_FailedSubscription_IsRecoveredOnNextSweepAsync() {
    // First subscribe attempt throws → after 1 failed attempt the state goes Failed
    // (InitialRetryAttempts=1, RetryIndefinitely=false). The health monitor (1 ms sweep)
    // resets the state and re-subscribes; the second attempt succeeds. The test waits on
    // the subscription's OnDisconnected hook, which the retry helper wires strictly AFTER
    // the state flips to Healthy — a deterministic completion signal.
    var transport = new EdgeTransport(failFirstBatchSubscribes: 1);
    var resilience = new SubscriptionResilienceOptions {
      InitialRetryAttempts = 1,
      InitialRetryDelay = TimeSpan.FromMilliseconds(1),
      RetryIndefinitely = false,
      AllowPartialSubscriptions = true,
      HealthCheckInterval = TimeSpan.FromMilliseconds(1),
    };
    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = _buildWorker(transport, _oneDestination(), resilience, sp);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    // Signal-based wait: the monitor's recovery subscribe creates the FIRST successful
    // subscription (the initial attempt threw). Then wait for its OnDisconnected hook,
    // which the retry helper wires strictly AFTER Status flips to Healthy.
    var recovered = await transport.FirstSubscriptionCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await recovered.Hooked.Task.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(transport.SubscribeBatchCallCount).IsGreaterThanOrEqualTo(2)
      .Because("Recovery requires a second subscribe attempt after the initial failure.");
    await Assert.That(worker.SubscriptionStates.Values.Single().Status).IsEqualTo(SubscriptionStatus.Healthy)
      .Because("The health monitor must return a Failed subscription to Healthy once the transport accepts the subscribe.");
    await Assert.That(worker.SubscriptionStates.Values.Single().AttemptCount).IsEqualTo(0)
      .Because("Recovery resets the attempt counter so future failures get a fresh retry budget.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Connection recovery handler
  // ============================================================

  [Test]
  public async Task ConnectionRecovered_DisposesOldSubscriptionsAndResubscribesAsync() {
    var transport = new RecoverableEdgeTransport();
    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = _buildWorker(transport, _oneDestination(), new SubscriptionResilienceOptions(), sp);

    await Assert.That(transport.RecoveryHandlerRegistered).IsTrue()
      .Because("The constructor must register the recovery handler on ITransportWithRecovery transports.");

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(transport.Subscriptions.TryDequeue(out var original)).IsTrue();

    // Invoke the recovery handler directly and await it — fully deterministic, no timers.
    await transport.SimulateRecoveryAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(original!.IsDisposed).IsTrue()
      .Because("Recovery must dispose the stale subscription before re-subscribing — leaking it would double-deliver.");
    await Assert.That(transport.SubscribeBatchCallCount).IsEqualTo(2)
      .Because("Recovery re-subscribes every destination exactly once.");
    var state = worker.SubscriptionStates.Values.Single();
    await Assert.That(state.Status).IsEqualTo(SubscriptionStatus.Healthy)
      .Because("After the recovery handler completes, the destination must be Healthy again.");
    await Assert.That(transport.Subscriptions.TryDequeue(out var replacement)).IsTrue();
    await Assert.That(ReferenceEquals(state.Subscription, replacement)).IsTrue()
      .Because("The state must track the NEW subscription, not the disposed one.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Pause / Resume
  // ============================================================

  [Test]
  public async Task PauseAndResumeAllSubscriptions_TogglesEverySubscriptionAsync() {
    var transport = new EdgeTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("edge-topic-1"));
    options.Destinations.Add(new TransportDestination("edge-topic-2"));
    var sp = new ServiceCollection().BuildServiceProvider();
    var worker = _buildWorker(transport, options, new SubscriptionResilienceOptions(), sp);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    var subs = transport.Subscriptions.ToArray();
    await Assert.That(subs).Count().IsEqualTo(2);

    await worker.PauseAllSubscriptionsAsync();
    foreach (var sub in subs) {
      await Assert.That(sub.PauseCalls).IsEqualTo(1)
        .Because("PauseAllSubscriptionsAsync must pause EVERY active subscription exactly once.");
      await Assert.That(sub.IsActive).IsFalse()
        .Because("Paused subscriptions must stop delivering messages.");
    }

    await worker.ResumeAllSubscriptionsAsync();
    foreach (var sub in subs) {
      await Assert.That(sub.ResumeCalls).IsEqualTo(1)
        .Because("ResumeAllSubscriptionsAsync must resume EVERY paused subscription exactly once.");
      await Assert.That(sub.IsActive).IsTrue()
        .Because("Resumed subscriptions must deliver messages again.");
    }

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Empty batch early-return + detached drain
  // ============================================================

  [Test]
  public async Task HandleBatch_EmptyMessageList_ReturnsWithoutTouchingCoordinatorAsync() {
    var transport = new EdgeTransport();
    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    var sp = services.BuildServiceProvider();
    var worker = _buildWorker(transport, _oneDestination(), new SubscriptionResilienceOptions(), sp);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    await transport.DeliverBatchAsync([]);

    await Assert.That(coordinator.StoreInboxCallCount).IsEqualTo(0)
      .Because("An empty transport batch must return before creating a scope or touching the coordinator.");

    // Boy-scout adjacency: an empty detached-task bag must drain instantly.
    await worker.DrainDetachedAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Owned-namespace hierarchy + self-echo
  // ============================================================

  private const string OWNED_COMMAND_ENVELOPE_TYPE =
    "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.Sub.Commands.DoThing, TestApp]], Whizbang.Core";

  [Test]
  public async Task HandleBatch_OwnedChildNamespaceSelfEcho_DiscardsWithoutStoringAsync() {
    // Owned domain "TestApp" must hierarchically match payload namespace
    // "TestApp.Sub.Commands"; the last hop carrying OUR service name marks it self-echo.
    var transport = new EdgeTransport();
    var coordinator = new NoOpWorkCoordinator();
    var routing = new RoutingOptions();
    routing.OwnDomains("TestApp");

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    var sp = services.BuildServiceProvider();
    var worker = _buildWorker(
      transport, _oneDestination(), new SubscriptionResilienceOptions(), sp,
      routingOptions: Options.Create(routing),
      instanceProvider: new EdgeInstanceProvider("edge-svc"));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    var envelope = _envelope(MessageId.New(), echoServiceName: "edge-svc");
    await transport.DeliverBatchAsync([new TransportMessage(envelope, OWNED_COMMAND_ENVELOPE_TYPE)]);

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(0)
      .Because("A self-echo command in an owned child namespace must be discarded at the receive boundary — storing it would loop the command back into our own inbox.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task HandleBatch_OwnedNamespaceCommandFromOtherService_IsStoredAsync() {
    // Same owned namespace, but the last hop names a DIFFERENT service — owned commands
    // legitimately arrive from other services, so this must NOT be treated as echo.
    var transport = new EdgeTransport();
    var coordinator = new NoOpWorkCoordinator();
    var routing = new RoutingOptions();
    routing.OwnDomains("TestApp");

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    var sp = services.BuildServiceProvider();
    var worker = _buildWorker(
      transport, _oneDestination(), new SubscriptionResilienceOptions(), sp,
      routingOptions: Options.Create(routing),
      instanceProvider: new EdgeInstanceProvider("edge-svc"));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    var envelope = _envelope(MessageId.New(), echoServiceName: "some-other-svc");
    await transport.DeliverBatchAsync([new TransportMessage(envelope, OWNED_COMMAND_ENVELOPE_TYPE)]);

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1)
      .Because("Owned commands from OTHER services are the normal cross-service delivery case and must be stored.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task HandleBatch_OwnedNamespaceZeroHops_IsStoredNotDiscardedAsync() {
    // Zero hops → _isSelfEcho must return false (there is no origin to compare against),
    // so the message flows through to the inbox.
    var transport = new EdgeTransport();
    var coordinator = new NoOpWorkCoordinator();
    var routing = new RoutingOptions();
    routing.OwnDomains("TestApp");

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    var sp = services.BuildServiceProvider();
    var worker = _buildWorker(
      transport, _oneDestination(), new SubscriptionResilienceOptions(), sp,
      routingOptions: Options.Create(routing),
      instanceProvider: new EdgeInstanceProvider("edge-svc"));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.WaitForSubscriptionsReadyAsync().WaitAsync(TimeSpan.FromSeconds(5));

    var envelope = _envelope(MessageId.New(), echoServiceName: null);
    await transport.DeliverBatchAsync([new TransportMessage(envelope, OWNED_COMMAND_ENVELOPE_TYPE)]);

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1)
      .Because("With no hops there is no evidence of echo — the safe behavior is to accept the message.");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }
}
