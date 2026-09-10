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
using Whizbang.Core.Priority;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable IDE0060, RCS1163 // Unused parameters: the fake transport implements interface members the test never exercises

namespace Whizbang.Core.Tests.Priority;

/// <summary>Test event for the classification suite; top-level so the JSON source generator can fill in the context.</summary>
internal sealed record PriorityTestEvent(string Value);

[JsonSerializable(typeof(PriorityTestEvent))]
[JsonSerializable(typeof(MessageEnvelope<PriorityTestEvent>))]
[JsonSerializable(typeof(EnvelopeMetadata))]
internal sealed partial class PriorityTestJsonContext : JsonSerializerContext;

/// <summary>
/// The consumer classifies at its receive boundary (priority step 1): the effective number stored on the
/// inbox row is the declared number after the registered receive hooks, and an undeclared number lands in the
/// standard band. A host's own receive hook lowers or raises by its own rule; without any chain registered the
/// row still carries the declared number's effective value.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#declaration</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class ConsumerPriorityClassificationTests {

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

  private sealed class _lowerToBackground : IPriorityReceiveHook {
    public int Order => 2000;   // after the framework default, so it sees the accepted number and overrides it
    public int Classify(PriorityReceiveContext context) => WorkPriority.BACKGROUND;
  }

  private static async Task<InboxMessage> _receiveAsync(int declared, Action<IServiceCollection>? configure) {
    var transport = new CapturingTransport();
    var strategy = new RecordingStrategy();
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new FakeServiceInstanceProvider());
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    configure?.Invoke(services);
    await using var sp = services.BuildServiceProvider();
    var worker = new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      jsonOptions: new JsonSerializerOptions { TypeInfoResolver = PriorityTestJsonContext.Default },
      logger: NullLogger<ServiceBusConsumerWorker>.Instance,
      orderedProcessor: new OrderedStreamProcessor(),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      options: new ServiceBusConsumerOptions { Subscriptions = [new TopicSubscription("test-topic", "test-sub")] },
      envelopeSerializer: new StubEnvelopeSerializer(),
      receptorRegistry: new SubscribedRegistry());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    var envelope = new MessageEnvelope<PriorityTestEvent> {
      MessageId = MessageId.New(),
      Payload = new PriorityTestEvent("hi"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
      Priority = declared,
    };
    await transport.BatchHandler!.Invoke([new TransportMessage(envelope, typeof(MessageEnvelope<PriorityTestEvent>).AssemblyQualifiedName)], cts.Token);
    var stored = await strategy.Stored.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await worker.StopAsync(CancellationToken.None);
    return stored;
  }

  [Test]
  public async Task Receive_StoresTheDeclaredNumber_WhenTheDefaultChainAcceptsItAsync() {
    var stored = await _receiveAsync(declared: 42, configure: s => s.AddWhizbangPriority());
    await Assert.That(stored.Priority).IsEqualTo(42)
      .Because("the framework's receive default accepts the producer's declaration; the row is where the claim reads it from");
  }

  [Test]
  public async Task Receive_ReadsAnUndeclaredNumberAsStandardAsync() {
    var stored = await _receiveAsync(declared: WorkPriority.UNDECLARED, configure: s => s.AddWhizbangPriority());
    await Assert.That(stored.Priority).IsEqualTo(WorkPriority.STANDARD)
      .Because("a row is never stored with zero: the miss lands in the middle band, never the urgent one");
  }

  [Test]
  public async Task Receive_AHostReceiveHook_LowersTheNumber_AndTheRowCarriesItsAnswerAsync() {
    var stored = await _receiveAsync(declared: WorkPriority.INTERACTIVE, configure: s => {
      s.AddWhizbangPriority();
      s.AddSingleton<IPriorityReceiveHook, _lowerToBackground>();
    });
    await Assert.That(stored.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("each consumer is in control of its own situation; a secondary consumer may treat a producer's interactive message as background");
  }

  [Test]
  public async Task Receive_WithNoChainRegistered_StoresTheDeclaredNumbersEffectiveValueAsync() {
    var declared = await _receiveAsync(declared: 30, configure: null);
    var undeclared = await _receiveAsync(declared: WorkPriority.UNDECLARED, configure: null);
    await Assert.That(declared.Priority).IsEqualTo(30);
    await Assert.That(undeclared.Priority).IsEqualTo(WorkPriority.STANDARD)
      .Because("a host that never registered the chain still stores a usable effective number");
  }
}
