using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the composite echo-gate exemption: a composite travels over transport like an owned event and
/// fans out at EVERY destination service, including the publishing service via the self-loop. So unlike a
/// normal owned event (event-stored at publish time, hence its loopback echo is redundant and discarded),
/// an owned composite must NOT be echo-discarded — it has no publish-time event store and must survive to
/// the dispatch seam to fan out. <see cref="TransportConsumerWorker"/> consults
/// <see cref="IReceptorRegistryQuery.IsComposite"/> to make exactly that exception; everything downstream
/// reuses the existing receive-side fan-out (the children are stamped NoRebroadcast).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
/// <docs>fundamentals/messaging/composite-events#echo-gate</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class TransportConsumerWorkerOwnedCompositeEchoTests {

  private const string THIS_SERVICE = "JobService";
  private const string OWNED_NAMESPACE = "a consumer.Contracts.Job.BulkImport";

  // Wrapper envelope-type format the worker expects; the inner type is an owned-namespace composite.
  private const string COMPOSITE_ENVELOPE_TYPE =
    "Whizbang.Core.Observability.MessageEnvelope`1[[a consumer.Contracts.Job.BulkImport.OrderBulkImportComposite, a consumer.Contracts]], Whizbang.Core";
  private const string PLAIN_EVENT_ENVELOPE_TYPE =
    "Whizbang.Core.Observability.MessageEnvelope`1[[a consumer.Contracts.Job.BulkImport.SomethingHappenedEvent, a consumer.Contracts]], Whizbang.Core";

  [Test]
  public async Task OwnedCompositeSelfEcho_IsNotDiscarded_SurvivesToFanOutAsync() {
    // The composite loops back to its own service. Without the exemption it would be self-echo-discarded
    // and never fan out; with IsComposite=true it survives to the inbox.
    var (worker, transport, coordinator) = _build(isComposite: true);
    await _startAsync(worker, transport);

    await transport.SimulateBatchReceivedAsync([
      new TransportMessage(_selfEchoEnvelope(), COMPOSITE_ENVELOPE_TYPE),
    ]);

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1)
      .Because("an owned composite is exempt from echo-discard — it must reach the dispatch seam to fan out at its own service.");
  }

  [Test]
  public async Task OwnedPlainEventSelfEcho_IsStillDiscardedAsync() {
    // Control: the exemption is composite-specific. A normal owned event self-echo is still discarded
    // (it was already event-stored at publish time), proving the gate didn't go permissive for everything.
    var (worker, transport, coordinator) = _build(isComposite: false);
    await _startAsync(worker, transport);

    await transport.SimulateBatchReceivedAsync([
      new TransportMessage(_selfEchoEnvelope(), PLAIN_EVENT_ENVELOPE_TYPE),
    ]);

    await Assert.That(coordinator.StoredInboxCount).IsEqualTo(0)
      .Because("a non-composite owned-domain self-echo must still be discarded — the exemption applies only to composites.");
  }

  private static async Task _startAsync(TransportConsumerWorker worker, CapturingBatchTransport transport) {
    _ = worker.StartAsync(CancellationToken.None);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));
  }

  private static (TransportConsumerWorker worker, CapturingBatchTransport transport, NoOpWorkCoordinator coordinator)
      _build(bool isComposite) {
    var transport = new CapturingBatchTransport();
    var coordinator = new NoOpWorkCoordinator();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    services.Configure<RoutingOptions>(opts => { opts.OwnDomains(OWNED_NAMESPACE); });
    var sp = services.BuildServiceProvider();

    // Registry double: every type is an any-consumer (so the drop-gate keeps it), and IsComposite is what
    // the echo-gate exemption hinges on.
    var registry = new FakeReceptorRegistry(isComposite);
    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      sp.GetRequiredService<IServiceScopeFactory>(),
      new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null, metrics: null,
      NullLogger<TransportConsumerWorker>.Instance,
      routingOptions: sp.GetRequiredService<IOptions<RoutingOptions>>(),
      serviceInstanceProvider: new StubServiceInstanceProvider(THIS_SERVICE),
      receptorRegistry: registry);

    return (worker, transport, coordinator);
  }

  private static MessageEnvelope<JsonElement> _selfEchoEnvelope() => new() {
    MessageId = MessageId.New(),
    Payload = JsonDocument.Parse("{}").RootElement,
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        // Last hop = THIS service → self-echo (would be discarded without the composite exemption).
        ServiceInstance = new ServiceInstanceInfo {
          ServiceName = THIS_SERVICE,
          InstanceId = Guid.NewGuid(),
          HostName = "test-host",
          ProcessId = 1234,
        },
      },
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
  };

  private sealed class FakeReceptorRegistry(bool isComposite) : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => true;
    public bool HasAnyConsumer(string messageType) => true;
    public bool IsComposite(string messageType) => isComposite;
  }

  private sealed class StubServiceInstanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1234;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = serviceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class CapturingBatchTransport : ITransport, IDisposable {
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _batchHandler;
    private readonly SemaphoreSlim _subscribeSignal = new(0, int.MaxValue);

    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination, CancellationToken cancellationToken = default)
      => Task.FromResult<ISubscription>(new _NopSubscription());
    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination, TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      _batchHandler = batchHandler;
      _subscribeSignal.Release();
      return Task.FromResult<ISubscription>(new _NopSubscription());
    }
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope envelope,
        TransportDestination destination, CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull
      => throw new NotImplementedException();
    public void Dispose() => _subscribeSignal.Dispose();

    public async Task WaitForSubscriptionAsync(TimeSpan timeout) {
      if (!await _subscribeSignal.WaitAsync(timeout)) {
        throw new TimeoutException($"Subscription not created within {timeout}");
      }
    }

    public async Task SimulateBatchReceivedAsync(IReadOnlyList<TransportMessage> batch) {
      if (_batchHandler is null) {
        throw new InvalidOperationException("SubscribeBatchAsync was never called by the worker.");
      }
      await _batchHandler(batch, CancellationToken.None);
    }

    private sealed class _NopSubscription : ISubscription {
      public bool IsActive => true;
#pragma warning disable CS0067
      public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067
      public Task PauseAsync() => Task.CompletedTask;
      public Task ResumeAsync() => Task.CompletedTask;
      public void Dispose() { }
    }
  }
}
