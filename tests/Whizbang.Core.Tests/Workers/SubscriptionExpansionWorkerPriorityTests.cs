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
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Priority step 1, background work: a subscription expansion asks every origin for history this service never
/// received. The backfill request it broadcasts declares <see cref="WorkPriority.BACKGROUND"/> on its envelope,
/// so origins serve the backfill behind their live work.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#background-work</docs>
/// <code-under-test>src/Whizbang.Core/Workers/SubscriptionExpansionWorker.cs</code-under-test>
[Category("Unit")]
public class SubscriptionExpansionWorkerPriorityTests {
  private static readonly string _expandedType = TypeNameFormatter.FormatClrTypeName(typeof(SubscriptionExpansionWorkerTests.ExpandedEvent));

  [Test]
  public async Task Expansion_TheBackfillRequestIsBackgroundAsync() {
    var coordinator = new _registryCoordinator();
    coordinator.Registry["Contracts.PriorType"] = ConsumedTypeBackfillStatus.Baseline;
    var transport = new _captureTransport();
    var worker = _buildWorker(coordinator, transport, new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped });

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Registry[_expandedType]).IsEqualTo(ConsumedTypeBackfillStatus.Requested);
    await Assert.That(transport.Published.Single().Envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("a backfill of whole-type history is the definition of catch-up work; it must never be served ahead of an origin's live traffic");
  }

  [Test]
  public async Task Expansion_InsideAnInteractiveHandling_TheRequestStaysBackgroundAsync() {
    var coordinator = new _registryCoordinator();
    coordinator.Registry["Contracts.PriorType"] = ConsumedTypeBackfillStatus.Baseline;
    var transport = new _captureTransport();
    var worker = _buildWorker(coordinator, transport, new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped });

    using (PriorityContext.Enter(WorkPriority.INTERACTIVE)) {
      await worker.RunOnceAsync(CancellationToken.None);
    }

    await Assert.That(transport.Published.Single().Envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the worker publishes straight to the transport; the band is declared where the envelope is built, not inherited");
  }

  private static SubscriptionExpansionWorker _buildWorker(
      _registryCoordinator coordinator, _captureTransport transport, StreamIntegrityOptions options) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IEventTypeProvider>(new _typeProvider());
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider("expanded-svc"));
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new SubscriptionExpansionWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(options),
      NullLogger<SubscriptionExpansionWorker>.Instance);
  }

  private sealed class _typeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(SubscriptionExpansionWorkerTests.ExpandedEvent)];
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

  private sealed class _registryCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public Dictionary<string, ConsumedTypeBackfillStatus> Registry { get; } = [];
    public Task<IReadOnlyList<ConsumedTypeRegistration>> GetConsumedTypeRegistrationsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<ConsumedTypeRegistration>>(
        [.. Registry.Select(kv => new ConsumedTypeRegistration { EventType = kv.Key, Status = kv.Value })]);
    public Task RegisterConsumedTypesAsync(IReadOnlyList<string> eventTypes, bool asBaseline, CancellationToken cancellationToken = default) {
      foreach (var type in eventTypes) {
        Registry.TryAdd(type, asBaseline ? ConsumedTypeBackfillStatus.Baseline : ConsumedTypeBackfillStatus.Pending);
      }
      return Task.CompletedTask;
    }
    public Task MarkConsumedTypeBackfillRequestedAsync(IReadOnlyList<string> eventTypes, CancellationToken cancellationToken = default) {
      foreach (var type in eventTypes) {
        if (Registry.TryGetValue(type, out var status) && status == ConsumedTypeBackfillStatus.Pending) {
          Registry[type] = ConsumedTypeBackfillStatus.Requested;
        }
      }
      return Task.CompletedTask;
    }
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
