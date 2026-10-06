// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text.Json;
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
/// Branches of <see cref="RepairDrainWorker"/> the sibling suites leave untaken: the options guard, a
/// host with no service identity to sign requests with, a window starting at the very first commit,
/// and the drain's request counter when stream-integrity metrics are registered.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/RepairDrainWorker.cs</code-under-test>
[NotInParallel(Order = 106)]
[Category("Workers")]
public class RepairDrainWorkerBranchCoverageTests {

  private sealed class NullValueOptions : IOptions<StreamIntegrityOptions> {
    public StreamIntegrityOptions Value => null!;
  }

  private sealed class DrainCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<IntegrityRepairDrainItem> Eligible { get; } = [];
    public int ClaimCalls { get; private set; }

    public Task<IReadOnlyList<IntegrityRepairDrainItem>> IntegrityClaimRepairDrainAsync(
        IReadOnlyList<Guid> originIds, DateTimeOffset now, TimeSpan baseBackoff, int maxAttempts,
        int limit, CancellationToken cancellationToken = default) {
      ClaimCalls++;
      var take = Eligible.Where(e => originIds.Contains(e.OriginServiceId)).Take(limit).ToList();
      foreach (var item in take) {
        Eligible.Remove(item);
      }
      return Task.FromResult<IReadOnlyList<IntegrityRepairDrainItem>>(take);
    }
  }

  private sealed class CaptureTransport : ITransport {
    public List<IMessageEnvelope> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      lock (Published) { Published.Add(envelope); }
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  private sealed class InstanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.New().Value;
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private static readonly StreamIntegrityOptions _autoRepair = new() {
    RepairMode = IntegrityRepairMode.AutoRepairCapped,
    RepairDrainRatePerSecond = 10,
  };

  private static (RepairDrainWorker Worker, DrainCoordinator Coordinator, CaptureTransport Transport, ServiceProvider Services) _build(
      Guid origin, bool registerInstance = true, StreamIntegrityMetrics? metrics = null) {
    var coordinator = new DrainCoordinator();
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(origin, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton(tracker);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    if (registerInstance) {
      services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider("drainer-svc"));
    }
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    var worker = new RepairDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new SchemaReadyGate(),
      Options.Create(_autoRepair),
      NullLogger<RepairDrainWorker>.Instance);
    return (worker, coordinator, transport, sp);
  }

  private static RequestRedeliveryCommand _deserializeRedelivery(IMessageEnvelope envelope) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return (RequestRedeliveryCommand)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestRedeliveryCommand)))!;
  }

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    await Assert.That(() => new RepairDrainWorker(
        sp.GetRequiredService<IServiceScopeFactory>(),
        SchemaReadyGate.AlreadyReady(),
        null!,
        NullLogger<RepairDrainWorker>.Instance))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }

  [Test]
  public async Task Constructor_OptionsWithNullValue_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    await Assert.That(() => new RepairDrainWorker(
        sp.GetRequiredService<IServiceScopeFactory>(),
        SchemaReadyGate.AlreadyReady(),
        new NullValueOptions(),
        NullLogger<RepairDrainWorker>.Instance))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }

  [Test]
  public async Task DrainTick_NoServiceInstanceProvider_NeverClaimsAsync() {
    // A request must name the service that asked, so the origin can route its answer back. Without
    // a service identity nothing could be sent, so nothing may be claimed (a claim burns an attempt).
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, sp) = _build(origin, registerInstance: false);
    await using var ownedServices = sp;
    coordinator.Eligible.Add(
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 1, 10));

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    await Assert.That(coordinator.ClaimCalls).IsEqualTo(0)
      .Because("with no requester name the tick must return before claiming");
    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task DrainTick_WindowStartingAtTheFirstCommit_AsksWithAnOpenFloorAsync() {
    // The floor is exclusive, so a window that starts at sequence 0 has no sequence below it to name:
    // the request leaves the floor open instead of sending -1.
    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, sp) = _build(origin);
    await using var ownedServices = sp;
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 0, 300),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 50, 500),
    ]);

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);

    await Assert.That(transport.Published).Count().IsEqualTo(1);
    var request = _deserializeRedelivery(transport.Published[0]);
    await Assert.That(request.FromCommitSequence).IsNull()
      .Because("a union window starting at the first commit asks from the beginning");
    await Assert.That(request.ToCommitSequence).IsEqualTo(499L)
      .Because("the ceiling is still the inclusive conversion of the union window");
  }

  [Test]
  public async Task DrainTick_WithStreamIntegrityMetrics_CountsTheRowsRequestedPerOriginAsync() {
    await using var meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterServices.GetRequiredService<IMeterFactory>()));
    var readings = new List<(long Value, string? Source, string? Origin)>();
    using var listener = new MeterListener();
    // Pinned to this instance's instrument: parallel tests build the same meter name.
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, metrics.RepairsRequested.Instrument)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      string? source = null;
      string? originTag = null;
      foreach (var tag in tags) {
        if (tag.Key == "source") {
          source = tag.Value?.ToString();
        } else if (tag.Key == "origin") {
          originTag = tag.Value?.ToString();
        }
      }
      lock (readings) { readings.Add((value, source, originTag)); }
    });
    listener.Start();

    var origin = TrackedGuid.New().Value;
    var (worker, coordinator, transport, sp) = _build(origin, metrics: metrics);
    await using var ownedServices = sp;
    coordinator.Eligible.AddRange([
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 100, 500),
      new IntegrityRepairDrainItem(origin, "tenant-a", "Contracts.TypeA", TrackedGuid.New().Value, 200, 600),
    ]);

    await worker.DrainTickAsync(1.0, DateTimeOffset.UtcNow, CancellationToken.None);
    listener.RecordObservableInstruments();

    await Assert.That(transport.Published).Count().IsEqualTo(1);
    await Assert.That(readings).Contains((2L, "drain", "origin-svc"))
      .Because("each dispatched group adds its row count, tagged as drain traffic to that origin");
  }
}
