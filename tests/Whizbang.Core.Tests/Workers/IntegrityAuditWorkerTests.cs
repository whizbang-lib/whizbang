using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Stream-integrity Phases A + L: one audit cycle reports local perspective coverage gaps (with a
/// capped LOCAL rebuild at AutoRepairCapped) and sends a DIRECTED manifest request to every origin
/// the checkpoint tracker knows.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityAuditWorker.cs</code-under-test>
public class IntegrityAuditWorkerTests {

  public sealed record AuditProbeEvent : IEvent {
    [StreamId]
    public Guid Sid { get; init; }
  }

  [Test]
  public async Task LocalGaps_ReportAndDispatchCappedRebuildsAsync() {
    var coordinator = new _auditCoordinator {
      Gaps = [
        new PerspectiveCoverageGap { StreamId = TrackedGuid.NewMedo().Value, PerspectiveName = "OrdersPerspective", EventCount = 7 },
        new PerspectiveCoverageGap { StreamId = TrackedGuid.NewMedo().Value, PerspectiveName = "ItemsPerspective", EventCount = 3 },
      ]
    };
    var dispatcher = new _captureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, new _captureTransport(),
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, MaxAutoRebuildsPerAudit = 1 });

    await worker.RunAuditOnceAsync(CancellationToken.None);

    var reports = dispatcher.Published.Cast<PerspectiveCoverageGapDetected>().ToList();
    await Assert.That(reports.Count).IsEqualTo(2);
    await Assert.That(reports.Count(r => r.AutoRebuildRequested)).IsEqualTo(1)
      .Because("the rebuild cap is a hard per-cycle budget — the second gap reports without rebuilding.");
    var rebuild = (RebuildPerspectiveCommand)dispatcher.Sent.Single();
    await Assert.That(rebuild.PerspectiveNames!).IsEquivalentTo(["OrdersPerspective"]);
    await Assert.That(rebuild.IncludeStreamIds!).IsEquivalentTo([coordinator.Gaps[0].StreamId])
      .Because("the rebuild is scoped to exactly the uncovered stream — local repair, minimal blast radius.");
  }

  [Test]
  public async Task ReportOnly_ReportsGapsWithoutRebuildingAsync() {
    var coordinator = new _auditCoordinator {
      Gaps = [new PerspectiveCoverageGap { StreamId = TrackedGuid.NewMedo().Value, PerspectiveName = "OrdersPerspective", EventCount = 7 }]
    };
    var dispatcher = new _captureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, new _captureTransport(), new StreamIntegrityOptions());

    await worker.RunAuditOnceAsync(CancellationToken.None);

    var report = (PerspectiveCoverageGapDetected)dispatcher.Published.Single();
    await Assert.That(report.AutoRebuildRequested).IsFalse()
      .Because("the ladder default is ReportOnly — an operator decides what to rebuild.");
    await Assert.That(dispatcher.Sent).IsEmpty();
  }

  [Test]
  public async Task KnownOrigins_GetDirectedManifestRequestsAsync() {
    var coordinator = new _auditCoordinator();
    var tracker = new IntegrityGapTracker();
    var originA = TrackedGuid.NewMedo().Value;
    var originB = TrackedGuid.NewMedo().Value;
    tracker.RecordCheckpoint(originA, "origin-a", DateTimeOffset.UtcNow);
    tracker.RecordCheckpoint(originB, "origin-b", DateTimeOffset.UtcNow);
    var transport = new _captureTransport();
    var worker = _buildWorker(coordinator, new _captureDispatcher(), transport, new StreamIntegrityOptions(), tracker);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published.Count).IsEqualTo(2);
    await Assert.That(transport.Published.Select(p => p.Envelope.Target!).ToList())
      .IsEquivalentTo(["origin-a", "origin-b"])
      .Because("each known origin gets its own DIRECTED manifest request — the tracker's origin set " +
               "IS the audit's origin set.");
    var options = JsonContextRegistry.CreateCombinedOptions();
    var request = (RequestIntegrityManifest)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)transport.Published[0].Envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestIntegrityManifest)))!;
    await Assert.That(request.RequesterService).IsEqualTo("auditor-svc");
    await Assert.That(request.EventTypes!).IsEquivalentTo([TypeNameFormatter.FormatClrTypeName(typeof(AuditProbeEvent))])
      .Because("the request restricts the manifest to the types this consumer actually subscribes to.");
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static IntegrityAuditWorker _buildWorker(
      _auditCoordinator coordinator, _captureDispatcher dispatcher, _captureTransport transport,
      StreamIntegrityOptions options, IntegrityGapTracker? tracker = null) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton(tracker ?? new IntegrityGapTracker());
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider("auditor-svc"));
    services.AddSingleton<IEventTypeProvider>(new _typeProvider());
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    return new IntegrityAuditWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new SchemaReadyGate(),
      Options.Create(options),
      NullLogger<IntegrityAuditWorker>.Instance);
  }

  private sealed class _typeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(AuditProbeEvent)];
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

  private sealed class _auditCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<PerspectiveCoverageGap> Gaps { get; init; } = [];

    public Task<IReadOnlyList<PerspectiveCoverageGap>> GetPerspectiveCoverageGapsAsync(
      TimeSpan settleWindow, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<PerspectiveCoverageGap>>(Gaps);
  }

  private sealed class _captureDispatcher : FakeDispatcher, IDispatcher {
    public List<object> Published { get; } = [];
    public List<object> Sent { get; } = [];

    public new Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
      Published.Add(eventData!);
      return Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());
    }

    public new Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull {
      Sent.Add(message);
      return Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());
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
