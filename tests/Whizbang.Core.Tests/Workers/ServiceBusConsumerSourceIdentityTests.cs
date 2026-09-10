using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable IDE0060, RCS1163 // Unused parameters: the fake transport implements interface members the test never exercises

namespace Whizbang.Core.Tests.Workers;

/// <summary>Test event for the source-identity suite; top-level so the JSON source generator can fill in the context.</summary>
internal sealed record SourceIdentityTestEvent(string Value);

[JsonSerializable(typeof(SourceIdentityTestEvent))]
[JsonSerializable(typeof(MessageEnvelope<SourceIdentityTestEvent>))]
[JsonSerializable(typeof(EnvelopeMetadata))]
internal sealed partial class SourceIdentityTestJsonContext : JsonSerializerContext;

/// <summary>
/// The inbox row a Service Bus consumer stores names the PRODUCING service (#739). The consumer worker
/// built its <see cref="InboxMessage"/> without the envelope's source identity, so every row defaulted to
/// the zero id and the store's fallback stamped the consumer's own service id: three consumers of one
/// producer each recorded a different constant, and fan-out could not be attributed. The two receive
/// paths (this worker and <see cref="TransportConsumerWorker"/>) now build the row through one helper.
/// </summary>
/// <docs>messaging/inbox-pattern#source-identity</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class ServiceBusConsumerSourceIdentityTests {

  private sealed class CapturingTransport : ITransport {
    public Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? BatchHandler { get; private set; }
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public bool IsInitialized => true;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, string?, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default)
      => Task.FromResult<ISubscription>(new _nop());
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) {
      BatchHandler = batchHandler;
      return Task.FromResult<ISubscription>(new _nop());
    }
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope envelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
    public void Dispose() { }
    private sealed class _nop : ISubscription {
      public bool IsActive { get; private set; } = true;
#pragma warning disable CS0067
      public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067
      public Task PauseAsync() { IsActive = false; return Task.CompletedTask; }
      public Task ResumeAsync() { IsActive = true; return Task.CompletedTask; }
      public void Dispose() { IsActive = false; }
    }
  }

  private sealed class RecordingStrategy : IWorkCoordinatorStrategy {
    public TaskCompletionSource<InboxMessage> Stored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) => Stored.TrySetResult(message);
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
  }

  private sealed class SubscribedRegistry : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => true;
    public bool HasAnyConsumer(string messageType) => true;
  }

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "consumer-svc";
    public string HostName => "consumer-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class StubEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonEnvelope = new MessageEnvelope<JsonElement> {
        MessageId = envelope.MessageId,
        Payload = JsonSerializer.SerializeToElement(new { }),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
      };
      return new SerializedEnvelope(jsonEnvelope, typeof(MessageEnvelope<>).MakeGenericType(typeof(TMessage)).AssemblyQualifiedName!, typeof(TMessage).AssemblyQualifiedName!);
    }
    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) => throw new NotSupportedException();
  }

  [Test]
  public async Task HandleMessage_StoresTheProducersServiceIdAndCommitSequence_FromTheEnvelopeAsync() {
    var producer = (Guid)TrackedGuid.NewMedo();
    var transport = new CapturingTransport();
    var strategy = new RecordingStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new FakeServiceInstanceProvider());
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    await using var sp = services.BuildServiceProvider();
    var worker = new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: new JsonSerializerOptions { TypeInfoResolver = SourceIdentityTestJsonContext.Default },
      logger: NullLogger<ServiceBusConsumerWorker>.Instance,
      orderedProcessor: new OrderedStreamProcessor(),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      options: new ServiceBusConsumerOptions { Subscriptions = [new TopicSubscription("test-topic", "test-sub")] },
      envelopeSerializer: new StubEnvelopeSerializer(),
      receptorRegistry: new SubscribedRegistry());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    var envelope = new MessageEnvelope<SourceIdentityTestEvent> {
      MessageId = MessageId.New(),
      Payload = new SourceIdentityTestEvent("hi"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
      SourceServiceId = producer,
      SourceCommitSequence = 42,
    };
    await transport.BatchHandler!.Invoke([new TransportMessage(envelope, typeof(MessageEnvelope<SourceIdentityTestEvent>).AssemblyQualifiedName)], cts.Token);

    var stored = await strategy.Stored.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(stored.SourceServiceId).IsEqualTo(producer)
      .Because("the row names the service that emitted the message; a zero here is stamped with the consumer's own id by the store, and every consumer then reads as its own producer (#739)");
    await Assert.That(stored.SourceCommitSequence).IsEqualTo(42L);

    await worker.StopAsync(CancellationToken.None);
  }
}
