using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks Phase A increment 3 of plans/composite-events-turnkey.md: the transport consumer no longer
/// expands composites at the wire edge. A composite arriving via transport is stored as a single
/// ordinary inbox row; fan-out moves to the dispatch seam (InboxDispatchWorker), inside the durable
/// retry/DLQ envelope. Previously the worker fanned the composite into N inbox rows here, which
/// orphaned the composite from durability/retry.
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class TransportConsumerWorkerCompositeNoExpandTests {

  private sealed class CapturingBatchTransport : ITransport {
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _batchHandler;
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
      return Task.FromResult<ISubscription>(new _NopSubscription());
    }
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope envelope,
        TransportDestination destination, CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull
      => throw new NotImplementedException();
    public void Dispose() { }

    public async Task SimulateBatchReceivedAsync(IReadOnlyList<TransportMessage> batch) {
      if (_batchHandler is null) {
        throw new InvalidOperationException("SubscribeBatchAsync was never called by the worker.");
      }
      await _batchHandler(batch, CancellationToken.None);
    }

    private sealed class _NopSubscription : ISubscription {
      public bool IsActive { get; private set; } = true;
#pragma warning disable CS0067
      public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067
      public Task PauseAsync() { IsActive = false; return Task.CompletedTask; }
      public Task ResumeAsync() { IsActive = true; return Task.CompletedTask; }
      public void Dispose() { IsActive = false; }
    }
  }

  private sealed class AlwaysConsumedRegistry : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => true;
    public bool HasAnyConsumer(string messageType) => true;
  }

  // Minimal serializer used by _serializeToNewInboxMessage's strongly-typed branch (invoked
  // reflectively as SerializeEnvelope<TConcrete>). Records the payload's runtime AQN as MessageType.
  private sealed class FakeEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var aqn = envelope.Payload!.GetType().AssemblyQualifiedName!;
      var jsonEnv = new MessageEnvelope<JsonElement> {
        DispatchContext = envelope.DispatchContext,
        MessageId = envelope.MessageId,
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = envelope.Hops?.ToList() ?? [],
      };
      return new SerializedEnvelope(jsonEnv, $"Whizbang.Core.Observability.MessageEnvelope`1[[{aqn}]], Whizbang.Core", aqn);
    }
    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
  }

  private sealed record _innerEvent(string Id) : IEvent;

  private sealed class _composite(params _innerEvent[] inner) : ICompositeEvent {
    public int MaxInnerEventsAllowed => 10_000;
    public IEnumerable<IMessage> InnerEvents => inner;
  }

  private static readonly string _compositeEnvelopeType =
    $"Whizbang.Core.Observability.MessageEnvelope`1[[{typeof(_composite).AssemblyQualifiedName}]], Whizbang.Core";

  [Test]
  public async Task CompositeFromTransport_StoredAsSingleInboxRow_NotExpandedAsync() {
    var transport = new CapturingBatchTransport();
    var coordinator = new NoOpWorkCoordinator();
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IEnvelopeSerializer>(new FakeEnvelopeSerializer());
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();

    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      sp.GetRequiredService<IServiceScopeFactory>(),
      new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null, metrics: null,
      NullLogger<TransportConsumerWorker>.Instance,
      receptorRegistry: new AlwaysConsumedRegistry(),
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider());

    await using (sp) {
      using var cts = new CancellationTokenSource();
      _ = worker.StartAsync(cts.Token);
      await Task.Delay(150);

      var compositeEnvelope = new MessageEnvelope<_composite> {
        MessageId = MessageId.New(),
        Payload = new _composite(new _innerEvent("J-1"), new _innerEvent("J-2"), new _innerEvent("J-3")),
        Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UtcNow, ServiceInstance = ServiceInstanceInfo.Unknown }],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      };
      await transport.SimulateBatchReceivedAsync([new TransportMessage(compositeEnvelope, _compositeEnvelopeType)]);

      cts.Cancel();

      await Assert.That(coordinator.StoreInboxCallCount).IsEqualTo(1);
      await Assert.That(coordinator.StoreInboxBatchSizes).IsEquivalentTo([1])
        .Because("The composite is stored as a SINGLE inbox row — fan-out moved to the dispatch seam; the transport edge no longer expands it into N children.");
      await Assert.That(coordinator.StoredInboxCount).IsEqualTo(1);
    }
  }
}
