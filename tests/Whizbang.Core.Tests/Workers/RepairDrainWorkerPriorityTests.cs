using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Priority step 1, background work: the repair drain turns ledger rows into redelivery requests at a metered
/// rate; nobody waits on any of them. Every request it publishes declares <see cref="WorkPriority.BACKGROUND"/>
/// on its envelope, so the origin serves repairs behind its live work.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#background-work</docs>
/// <code-under-test>src/Whizbang.Core/Workers/RepairDrainWorker.cs</code-under-test>
[Category("Unit")]
public class RepairDrainWorkerPriorityTests {
  [Test]
  public async Task DrainTick_EveryRepairRequestIsBackgroundAsync() {
    var origin = TrackedGuid.NewMedo().Value;
    var (worker, coordinator, transport) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
    }, origin);
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 100, 500),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeB", TrackedGuid.NewMedo().Value, 300, 700),
    ]);

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    await Assert.That(transport.Published.Count).IsEqualTo(2);
    foreach (var (envelope, _, _) in transport.Published) {
      await Assert.That(envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
        .Because("a repair request is served from the origin's inbox; at the standard number it would be claimed ahead of the origin's live standard work");
    }
  }

  [Test]
  public async Task DrainTick_InsideAnInteractiveHandling_TheRequestStaysBackgroundAsync() {
    var origin = TrackedGuid.NewMedo().Value;
    var (worker, coordinator, transport) = _build(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      RepairDrainRatePerSecond = 10,
    }, origin);
    coordinator.Eligible.Add(new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 100, 500));

    using (PriorityContext.Enter(WorkPriority.INTERACTIVE)) {
      await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    await Assert.That(transport.Published.Single().Envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the worker builds the envelope by hand and publishes straight to the transport; no ambient number applies to it");
  }

  private static (RepairDrainWorker Worker, _drainCoordinator Coordinator, _captureTransport Transport) _build(
      StreamIntegrityOptions options, Guid origin) {
    var coordinator = new _drainCoordinator();
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(origin, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton(tracker);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider("drainer-svc"));
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    var worker = new RepairDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new SchemaReadyGate(),
      Options.Create(options),
      NullLogger<RepairDrainWorker>.Instance);
    return (worker, coordinator, transport);
  }

  private sealed class _drainCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<IntegrityRepairDrainItem> Eligible { get; } = [];
    public Task<IReadOnlyList<IntegrityRepairDrainItem>> IntegrityClaimRepairDrainAsync(
        IReadOnlyList<Guid> originIds, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
        int limit, CancellationToken cancellationToken = default) {
      var take = Eligible.Where(e => originIds.Contains(e.OriginServiceId)).Take(limit).ToList();
      foreach (var item in take) {
        Eligible.Remove(item);
      }
      return Task.FromResult<IReadOnlyList<IntegrityRepairDrainItem>>(take);
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
}
