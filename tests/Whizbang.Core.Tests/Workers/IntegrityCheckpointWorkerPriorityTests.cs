using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.Routing;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Priority step 1, background work: the integrity checkpoint is a control signal the next cycle supersedes.
/// Whichever way it leaves the origin, the copies published straight to the transport carry
/// <see cref="WorkPriority.BACKGROUND"/> on their envelope, and the dispatcher fallback runs inside a background
/// handling so the producer hooks inherit the same band. A checkpoint at the standard number would be claimed
/// ahead of a consumer's own standard work.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#background-work</docs>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityCheckpointWorker.cs</code-under-test>
[Category("Unit")]
public class IntegrityCheckpointWorkerPriorityTests {
  [Test]
  public async Task RunCheckpointOnce_WithTransport_EveryTopicCopyIsBackgroundAsync() {
    var ordersType = typeof(CheckpointTopicProbes.Orders.OrdersProbeEvent);
    var usersType = typeof(CheckpointTopicProbes.Users.UsersProbeEvent);
    var coordinator = new _checkpointCoordinator {
      Window = new IntegrityCheckpointWindow {
        FromCommitSequence = 5,
        ToCommitSequence = 9,
        Buckets = [new CheckpointBucket { TenantScope = "tenant-a", EventType = TypeNameFormatter.Format(ordersType), Count = 3 }]
      },
      OwnAuditedEventTypes = [TypeNameFormatter.Format(ordersType), TypeNameFormatter.Format(usersType)],
    };
    var transport = new _captureTransport();
    var worker = _buildWorker(coordinator, new _captureDispatcher(), transport, new _catalog(ordersType, usersType));

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published.Count).IsEqualTo(2);
    foreach (var (envelope, _, _) in transport.Published) {
      await Assert.That(envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
        .Because("each topic copy is the same control signal; every consumer must receive it declared background");
    }
  }

  [Test]
  public async Task RunCheckpointOnce_DispatcherFallback_PublishesInsideABackgroundHandlingAsync() {
    var coordinator = new _checkpointCoordinator {
      Window = new IntegrityCheckpointWindow { FromCommitSequence = 0, ToCommitSequence = 4, Buckets = [] },
    };
    var dispatcher = new _captureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, transport: null, catalog: null);

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    var (message, parent) = dispatcher.Published.Single();
    await Assert.That(message).IsTypeOf<IntegrityCheckpoint>();
    await Assert.That(parent).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the fallback goes through the dispatcher's producer hooks, whose default outside any handling is interactive; the worker must enter a background handling first");
  }

  [Test]
  public async Task RunCheckpointOnce_DoesNotLeaveTheBackgroundHandlingBehindAsync() {
    var coordinator = new _checkpointCoordinator {
      Window = new IntegrityCheckpointWindow { FromCommitSequence = 0, ToCommitSequence = 4, Buckets = [] },
    };
    var worker = _buildWorker(coordinator, new _captureDispatcher(), transport: null, catalog: null);

    await worker.RunCheckpointOnceAsync(CancellationToken.None);

    await Assert.That(PriorityContext.CurrentParent).IsEqualTo(WorkPriority.UNDECLARED)
      .Because("the handling the worker enters for its own publish is scoped to the cycle; a leaked parent would background every later dispatch on the flow");
  }

  private static IntegrityCheckpointWorker _buildWorker(
      _checkpointCoordinator coordinator, _captureDispatcher dispatcher, _captureTransport? transport, IMessageTypeCatalog? catalog) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider("origin-svc"));
    if (transport is not null) {
      services.AddSingleton<ITransport>(transport);
      services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
      services.AddSingleton<IOutboxRoutingStrategy>(new DomainTopicOutboxStrategy());
      var consumerOptions = new TransportConsumerOptions();
      consumerOptions.Destinations.Add(new TransportDestination("origin.requests"));
      services.AddSingleton(consumerOptions);
    }
    if (catalog is not null) {
      services.AddSingleton(catalog);
    }
    var sp = services.BuildServiceProvider();
    return new IntegrityCheckpointWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new SchemaReadyGate(),
      Options.Create(new StreamIntegrityOptions()),
      NullLogger<IntegrityCheckpointWorker>.Instance);
  }

  private sealed class _checkpointCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public IntegrityCheckpointWindow? Window { get; init; }
    public Guid LocalServiceId { get; } = TrackedGuid.NewMedo().Value;
    public List<string> OwnAuditedEventTypes { get; init; } = [];
    public Task<IReadOnlyList<string>> GetOwnAuditedEventTypesAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<string>>([.. OwnAuditedEventTypes]);
    public Task<IntegrityCheckpointWindow?> AdvanceIntegrityCheckpointAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(Window);
    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(LocalServiceId);
  }

  /// <summary>Records what was published and the ambient parent the producer hooks would have inherited from.</summary>
  private sealed class _captureDispatcher : FakeDispatcher, IDispatcher {
    public List<(object Message, int AmbientParent)> Published { get; } = [];
    public new Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
      Published.Add((eventData!, PriorityContext.CurrentParent));
      return Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());
    }
  }

  private sealed class _instanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.NewMedo().Value;
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  private sealed class _catalog(params Type[] eventTypes) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() =>
      [.. eventTypes.Select(t => new MessageTypeCatalogEntry(t, TypeNameFormatter.Format(t), "event", null))];
  }

  private sealed class _captureTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      lock (Published) {
        Published.Add((envelope, destination, envelopeType));
      }
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }
}
