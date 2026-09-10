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
using Whizbang.Core.Priority;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Priority step 1, background work: the integrity audit is reconciliation nobody waits on. The manifest
/// requests it publishes straight to the transport declare <see cref="WorkPriority.BACKGROUND"/> on their
/// envelope, and the gap reports and rebuild commands it dispatches run inside a background handling so the
/// producer hooks inherit that band instead of the interactive default a worker would otherwise get.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#background-work</docs>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityAuditWorker.cs</code-under-test>
[Category("Unit")]
public class IntegrityAuditWorkerPriorityTests {
  [Test]
  public async Task LocalGaps_TheReportAndTheRebuild_AreDispatchedInsideABackgroundHandlingAsync() {
    var coordinator = new _auditCoordinator {
      Gaps = [new PerspectiveCoverageGap { StreamId = TrackedGuid.NewMedo().Value, PerspectiveName = "OrdersPerspective", EventCount = 7 }]
    };
    var dispatcher = new _captureDispatcher();
    var worker = _buildWorker(coordinator, dispatcher, new _captureTransport(),
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, MaxAutoRebuildsPerAudit = 1 });

    await worker.RunAuditOnceAsync(CancellationToken.None);

    var (report, reportParent) = dispatcher.Published.Single();
    await Assert.That(report).IsTypeOf<PerspectiveCoverageGapDetected>();
    await Assert.That(reportParent).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("a gap report is bookkeeping; dispatched from a worker with no ambient parent it would be declared interactive by the default hook");
    var (rebuild, rebuildParent) = dispatcher.Sent.Single();
    await Assert.That(rebuild).IsTypeOf<RebuildPerspectiveCommand>();
    await Assert.That(rebuildParent).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("a rebuild replays history behind live projections; everything its handler emits inherits this number");
  }

  [Test]
  public async Task KnownOrigins_TheManifestRequestIsBackgroundAsync() {
    var tracker = new IntegrityGapTracker();
    var origin = TrackedGuid.NewMedo().Value;
    tracker.RecordCheckpoint(origin, "origin-a", DateTimeOffset.UtcNow, "origin-a.requests");
    var transport = new _captureTransport();
    var worker = _buildWorker(new _auditCoordinator(), new _captureDispatcher(), transport, new StreamIntegrityOptions(), tracker);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    var (envelope, _, _) = transport.Published.Single();
    await Assert.That(envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the origin answers the request in its own inbox; a request at the standard number would be served ahead of the origin's live standard work");
  }

  [Test]
  public async Task RunAuditOnce_LeavesNoBackgroundHandlingBehindAsync() {
    var worker = _buildWorker(new _auditCoordinator(), new _captureDispatcher(), new _captureTransport(), new StreamIntegrityOptions());

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(PriorityContext.CurrentParent).IsEqualTo(WorkPriority.UNDECLARED)
      .Because("the handling is scoped to the audit cycle; a leaked parent would background every later dispatch on the flow");
  }

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
    services.AddSingleton(new Whizbang.Core.Observability.StreamIntegrityMetrics(new Whizbang.Core.Observability.WhizbangMetrics()));
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
    public IReadOnlyList<Type> GetEventTypes() => [typeof(IntegrityAuditWorkerTests.AuditProbeEvent)];
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
    public Task<bool> TryClaimIntegrityAuditCycleAsync(TimeSpan claimWindow, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);
    public Task<long> GetIntegritySealAsync(Guid originServiceId, CancellationToken cancellationToken = default) =>
      Task.FromResult(0L);
    public Task<EpochVerificationResult> VerifyDigestEpochsAsync(TimeSpan settleWindow, int maxEpochs, CancellationToken cancellationToken = default) =>
      Task.FromResult(new EpochVerificationResult(0, 0));
    public Task<IReadOnlyList<PerspectiveCoverageGap>> GetPerspectiveCoverageGapsAsync(TimeSpan settleWindow, int maxGaps, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<PerspectiveCoverageGap>>([.. Gaps.Take(maxGaps)]);
    public Task<DigestVerificationResult> VerifyDigestTableAsync(TimeSpan settleWindow, CancellationToken cancellationToken = default) =>
      Task.FromResult(new DigestVerificationResult { BucketsChecked = 0, DriftUpdated = 0, DriftRemoved = 0, DriftAdded = 0 });
  }

  /// <summary>Records each dispatch with the ambient parent the producer hooks would inherit from.</summary>
  private sealed class _captureDispatcher : FakeDispatcher, IDispatcher {
    public List<(object Message, int AmbientParent)> Published { get; } = [];
    public List<(object Message, int AmbientParent)> Sent { get; } = [];
    public new Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
      Published.Add((eventData!, PriorityContext.CurrentParent));
      return Task.FromResult<IDeliveryReceipt>(new FakeDeliveryReceipt());
    }
    public new Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull {
      Sent.Add((message, PriorityContext.CurrentParent));
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
