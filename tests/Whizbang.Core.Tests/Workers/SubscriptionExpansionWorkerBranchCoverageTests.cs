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
/// Branches of <see cref="SubscriptionExpansionWorker"/> the sibling suites leave untaken: the options
/// guard, a pending registration whose type has since left the catalog, and the backfill counter
/// when stream-integrity metrics are registered.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/SubscriptionExpansionWorker.cs</code-under-test>
[Category("Workers")]
public class SubscriptionExpansionWorkerBranchCoverageTests {

  public sealed record CatalogEvent : IEvent {
    [StreamId]
    public Guid Sid { get; init; }
  }

  private const string REMOVED_TYPE = "Contracts.RemovedSinceLastBoot";
  private static readonly string _catalogType = TypeNameFormatter.FormatClrTypeName(typeof(CatalogEvent));
  private static readonly string _catalogWireType = TypeNameFormatter.Format(typeof(CatalogEvent));

  private sealed class NullValueOptions : IOptions<StreamIntegrityOptions> {
    public StreamIntegrityOptions Value => null!;
  }

  private sealed class TypeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(CatalogEvent)];
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

  private sealed class RegistryCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
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

  private static (SubscriptionExpansionWorker Worker, ServiceProvider Services) _build(
      RegistryCoordinator coordinator, CaptureTransport transport, StreamIntegrityMetrics? metrics = null) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IEventTypeProvider>(new TypeProvider());
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider("expanded-svc"));
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new SubscriptionExpansionWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped }),
      NullLogger<SubscriptionExpansionWorker>.Instance);
    return (worker, sp);
  }

  private static RequestRedeliveryCommand _command(IMessageEnvelope envelope) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return (RequestRedeliveryCommand)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestRedeliveryCommand)))!;
  }

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    await Assert.That(() => new SubscriptionExpansionWorker(
        sp.GetRequiredService<IServiceScopeFactory>(),
        SchemaReadyGate.AlreadyReady(),
        null!,
        NullLogger<SubscriptionExpansionWorker>.Instance))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }

  [Test]
  public async Task Constructor_OptionsWithNullValue_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    await Assert.That(() => new SubscriptionExpansionWorker(
        sp.GetRequiredService<IServiceScopeFactory>(),
        SchemaReadyGate.AlreadyReady(),
        new NullValueOptions(),
        NullLogger<SubscriptionExpansionWorker>.Instance))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }

  [Test]
  public async Task PendingTypeNoLongerInTheCatalog_IsRequestedUnderItsRegisteredNameAsync() {
    // A prior boot left a type Pending that this build no longer consumes. There is no catalog entry
    // to map it to a wire form, so it travels as registered: an unmatched name is a no-op at the
    // origin, which is better than silently dropping the retry for a name that may still match.
    var coordinator = new RegistryCoordinator();
    coordinator.Registry[_catalogType] = ConsumedTypeBackfillStatus.Pending;
    coordinator.Registry[REMOVED_TYPE] = ConsumedTypeBackfillStatus.Pending;
    var transport = new CaptureTransport();
    var (worker, sp) = _build(coordinator, transport);
    await using var ownedServices = sp;

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published).Count().IsEqualTo(1);
    var command = _command(transport.Published[0]);
    await Assert.That(command.EventTypes).IsEquivalentTo([_catalogWireType, REMOVED_TYPE])
      .Because("a catalog type is sent in its wire form, and a type no longer in the catalog passes through unchanged");
  }

  [Test]
  public async Task BackfillRequested_WithStreamIntegrityMetrics_CountsThePendingTypesAsync() {
    using var meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterServices.GetRequiredService<IMeterFactory>()));
    var readings = new List<long>();
    using var listener = new MeterListener();
    // Pinned to this instance's instrument: parallel tests build the same meter name.
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, metrics.BackfillsRequested.Instrument)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => {
      lock (readings) { readings.Add(value); }
    });
    listener.Start();

    var coordinator = new RegistryCoordinator();
    coordinator.Registry[_catalogType] = ConsumedTypeBackfillStatus.Pending;
    coordinator.Registry[REMOVED_TYPE] = ConsumedTypeBackfillStatus.Pending;
    var (worker, sp) = _build(coordinator, new CaptureTransport(), metrics);
    await using var ownedServices = sp;

    await worker.RunOnceAsync(CancellationToken.None);
    listener.RecordObservableInstruments();

    await Assert.That(readings).Contains(2L)
      .Because("one backfill request covering two pending types counts both on the registered meter");
    await Assert.That(coordinator.Registry[_catalogType]).IsEqualTo(ConsumedTypeBackfillStatus.Requested);
  }
}
