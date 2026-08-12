using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The paced repair drain's tick, deterministically: token pacing gates the claim budget,
/// claimed rows dispatch grouped per (origin, tenant, type) with the union of their stamped
/// windows, pre-stamp rows widen to whole history, and unlearned origins are never claimed —
/// no attempt budget burns on a request that could not leave the process.
/// </summary>
/// <docs>proposals/paced-repair-drain</docs>
public class RepairDrainWorkerTests {

  [Test]
  public async Task DrainTick_DispatchesGroupedPerType_WithTheUnionWindowAsync() {
    var origin = TrackedGuid.NewMedo().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairDrainRatePerSecond = 10,
    }, origin, learnTopic: true);
    var s1 = TrackedGuid.NewMedo().Value;
    var s2 = TrackedGuid.NewMedo().Value;
    var s3 = TrackedGuid.NewMedo().Value;
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", s1, 100, 500),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", s2, 200, 600),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeB", s3, 300, 700),
    ]);

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    await Assert.That(transport.Published.Count).IsEqualTo(2)
      .Because("three rows in two (origin, tenant, type) groups → two directed requests");
    var typeA = transport.Published
      .Select(p => _deserializeRedelivery(p.Envelope))
      .Single(r => r.EventTypes!.Contains("Contracts.TypeA"));
    await Assert.That(typeA.StreamIds!.Count).IsEqualTo(2)
      .Because("one request carries the whole group's stream set");
    await Assert.That(typeA.FromCommitSequence).IsEqualTo(99L)
      .Because("the group's union window, exclusive floor — exactly the burst path's conversion");
    await Assert.That(typeA.ToCommitSequence).IsEqualTo(599L)
      .Because("inclusive ceiling of the union window");
  }

  [Test]
  public async Task DrainTick_TokensGateTheClaimBudget_AndSpendOnClaimAsync() {
    var origin = TrackedGuid.NewMedo().Value;
    var (worker, coordinator, _, _) = _build(new StreamIntegrityOptions {
      RepairDrainRatePerSecond = 2,
    }, origin, learnTopic: true);
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 1, 10),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 1, 10),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 1, 10),
    ]);

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);
    await Assert.That(coordinator.ClaimCalls.Count).IsEqualTo(1);
    await Assert.That(coordinator.ClaimCalls[0].Limit).IsEqualTo(2)
      .Because("one second at two rows per second buys exactly two claims");

    await worker.DrainTickAsync(0.0, DateTimeOffset.UtcNow, CancellationToken.None);
    await Assert.That(coordinator.ClaimCalls.Count).IsEqualTo(1)
      .Because("the tokens were spent on the claim — zero elapsed time refills nothing");
  }

  [Test]
  public async Task DrainTick_UnlearnedOrigins_AreNeverClaimedAsync() {
    var origin = TrackedGuid.NewMedo().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairDrainRatePerSecond = 10,
    }, origin, learnTopic: false);
    coordinator.Eligible.Add(
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 1, 10));

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    await Assert.That(coordinator.ClaimCalls).IsEmpty()
      .Because("no learned request topic → nothing could be sent → the claim must not burn attempts");
    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task DrainTick_PreStampRows_WidenTheAskToWholeHistoryAsync() {
    var origin = TrackedGuid.NewMedo().Value;
    var (worker, coordinator, transport, _) = _build(new StreamIntegrityOptions {
      RepairDrainRatePerSecond = 10,
    }, origin, learnTopic: true);
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, 100, 500),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.NewMedo().Value, null, null),
    ]);

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    var request = _deserializeRedelivery(transport.Published.Single().Envelope);
    await Assert.That(request.FromCommitSequence).IsNull()
      .Because("a pre-stamp row in the group means the compared range is unknown — whole history, the legacy semantics");
    await Assert.That(request.ToCommitSequence).IsNull();
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static (RepairDrainWorker Worker, _drainCoordinator Coordinator, _captureTransport Transport, IntegrityGapTracker Tracker) _build(
      StreamIntegrityOptions options, Guid origin, bool learnTopic) {
    var coordinator = new _drainCoordinator();
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    if (learnTopic) {
      tracker.RecordCheckpoint(origin, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    } else {
      tracker.RecordCheckpoint(origin, "origin-svc", DateTimeOffset.UtcNow, requestTopic: null);
    }
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
    return (worker, coordinator, transport, tracker);
  }

  private static RequestRedeliveryCommand _deserializeRedelivery(IMessageEnvelope envelope) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return (RequestRedeliveryCommand)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestRedeliveryCommand)))!;
  }

  private sealed class _drainCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<IntegrityRepairDrainItem> Eligible { get; } = [];
    public List<(IReadOnlyList<Guid> Origins, int Limit)> ClaimCalls { get; } = [];

    public Task<IReadOnlyList<IntegrityRepairDrainItem>> IntegrityClaimRepairDrainAsync(
        IReadOnlyList<Guid> originIds, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
        int limit, CancellationToken cancellationToken = default) {
      ClaimCalls.Add((originIds, limit));
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
    public Task<ISubscription> SubscribeAsync(Func<IMessageEnvelope, string?, CancellationToken, Task> handler, TransportDestination destination, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
