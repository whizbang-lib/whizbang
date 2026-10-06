// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Branch coverage for <see cref="IntegrityAuditWorker"/>: the options guard, a host that registers no
/// stream-integrity metrics (every counter call must be skipped while the cycle still does its work),
/// a host that registers them (drift attributed by kind), a host with no service-instance provider,
/// and the subscribed-type list when no event-type provider is available.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/IntegrityAuditWorker.cs</code-under-test>
public class IntegrityAuditWorkerBranchCoverageTests {

  // ── constructor guard ───────────────────────────────────────────────────

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();

    IntegrityAuditWorker act() => new(
      sp.GetRequiredService<IServiceScopeFactory>(),
      SchemaReadyGate.AlreadyReady(),
      null!,
      NullLogger<IntegrityAuditWorker>.Instance);

    var ex = await Assert.That(act).Throws<ArgumentNullException>();
    await Assert.That(ex!.ParamName).IsEqualTo("options");
  }

  [Test]
  public async Task Constructor_OptionsWithNullValue_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();

    IntegrityAuditWorker act() => new(
      sp.GetRequiredService<IServiceScopeFactory>(),
      SchemaReadyGate.AlreadyReady(),
      new NullValueOptions(),
      NullLogger<IntegrityAuditWorker>.Instance);

    var ex = await Assert.That(act).Throws<ArgumentNullException>()
      .Because("an IOptions wrapper with no value must fail at construction, not on the first audit cycle");
    await Assert.That(ex!.ParamName).IsEqualTo("options");
  }

  // ── no metrics registered ───────────────────────────────────────────────

  [Test]
  public async Task RunSweepOnce_NoMetricsRegistered_StillVerifiesAndReportsBucketAndEpochDriftAsync() {
    var coordinator = new AuditCoordinator {
      DigestResult = new DigestVerificationResult { BucketsChecked = 7, DriftUpdated = 2, DriftRemoved = 1, DriftAdded = 3 },
      EpochResult = new EpochVerificationResult(4, 2),
    };
    var logger = new MessageLogger();
    var worker = _buildWorker(coordinator, new CaptureDispatcher(), new CaptureTransport(),
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly }, logger: logger);

    await worker.RunSweepOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.VerifyCalls).IsEqualTo(1)
      .Because("the digest sweep must run whether or not anything is counting it");
    await Assert.That(coordinator.EpochVerifyCalls).IsEqualTo(1);
    await Assert.That(logger.Saw("drift")).IsTrue()
      .Because("without metrics the log line is the only trace of the healing, so it must still be written");
    await Assert.That(logger.Saw("epoch")).IsTrue();
  }

  [Test]
  public async Task RunAuditOnce_NoMetricsRegistered_StillRebuildsReportsAndRequestsManifestAsync() {
    var gapStream = TrackedGuid.New().Value;
    var coordinator = new AuditCoordinator {
      Gaps = [new PerspectiveCoverageGap { StreamId = gapStream, PerspectiveName = "OrdersPerspective", EventCount = 3 }]
    };
    var dispatcher = new CaptureDispatcher();
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(TrackedGuid.New().Value, "origin-a", DateTimeOffset.UtcNow, "origin-a.requests");
    var worker = _buildWorker(coordinator, dispatcher, transport,
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, MaxAutoRebuildsPerAudit = 1 },
      tracker);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    var rebuild = (RebuildPerspectiveCommand)dispatcher.Sent.Single();
    await Assert.That(rebuild.IncludeStreamIds?.Contains(gapStream) == true).IsTrue()
      .Because("the capped auto-rebuild is dispatched even when no counter records it");
    var reported = (PerspectiveCoverageGapDetected)dispatcher.Published.Single();
    await Assert.That(reported.AutoRebuildRequested).IsTrue();
    await Assert.That(transport.Published).Count().IsEqualTo(1)
      .Because("the cross-service manifest request is sent regardless of metrics");
  }

  // ── metrics registered ──────────────────────────────────────────────────

  [Test]
  [NotInParallel("Metrics")]
  public async Task RunSweepOnce_WithMetrics_CountsBucketsAndAttributesDriftByKindAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var coordinator = new AuditCoordinator {
      DigestResult = new DigestVerificationResult { BucketsChecked = 7, DriftUpdated = 2, DriftRemoved = 1, DriftAdded = 3 },
      EpochResult = new EpochVerificationResult(9, 5),
    };
    var worker = _buildWorker(coordinator, new CaptureDispatcher(), new CaptureTransport(),
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly }, metrics: metrics);

    await worker.RunSweepOnceAsync(CancellationToken.None);

    await Assert.That(_sum(helper, "whizbang.stream_integrity.digest_buckets_verified")).IsEqualTo(7d);
    await Assert.That(_sum(helper, "whizbang.stream_integrity.digest_drift_healed", "updated")).IsEqualTo(2d);
    await Assert.That(_sum(helper, "whizbang.stream_integrity.digest_drift_healed", "removed")).IsEqualTo(1d);
    await Assert.That(_sum(helper, "whizbang.stream_integrity.digest_drift_healed", "added")).IsEqualTo(3d);
    await Assert.That(_sum(helper, "whizbang.stream_integrity.digest_drift_healed", "epoch-refolded")).IsEqualTo(5d)
      .Because("refolded epochs are their own kind, so bucket drift and epoch drift stay distinguishable");
  }

  // ── cross-service half: requester identity and subscribed types ─────────

  [Test]
  public async Task RunAuditOnce_NoServiceInstanceProvider_SendsNoManifestRequestButRunsTheLocalHalfAsync() {
    var coordinator = new AuditCoordinator {
      Gaps = [new PerspectiveCoverageGap { StreamId = TrackedGuid.New().Value, PerspectiveName = "OrdersPerspective", EventCount = 1 }]
    };
    var dispatcher = new CaptureDispatcher();
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(TrackedGuid.New().Value, "origin-a", DateTimeOffset.UtcNow, "origin-a.requests");
    var logger = new MessageLogger();
    var worker = _buildWorker(coordinator, dispatcher, transport,
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly }, tracker,
      logger: logger, registerInstanceProvider: false);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(dispatcher.Published).Count().IsEqualTo(1)
      .Because("the local coverage half ran before the cross-service half was abandoned");
    await Assert.That(transport.Published).IsEmpty()
      .Because("a manifest request names its requester; with no service identity there is nobody to answer to");
    await Assert.That(logger.Saw("reached no origins")).IsFalse()
      .Because("missing infrastructure is a configuration, not a cold fleet, so it must not trigger the startup retry");
  }

  [Test]
  public async Task RunAuditOnce_NoEventTypeProvider_RequestCarriesNoTypeFilterAsync() {
    var transport = new CaptureTransport();
    var worker = _buildWorkerWithOneOrigin(transport, typeProvider: null);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    var request = _deserializeRequest(transport.Published.Single().Envelope);
    await Assert.That(request.EventTypes).IsNull()
      .Because("with no provider the request asks for every type rather than for an empty list");
  }

  [Test]
  public async Task RunAuditOnce_UnavailableEventTypeProvider_RequestCarriesNoTypeFilterAsync() {
    var transport = new CaptureTransport();
    var worker = _buildWorkerWithOneOrigin(transport, typeProvider: NullEventTypeProvider.Instance);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    var request = _deserializeRequest(transport.Published.Single().Envelope);
    await Assert.That(request.EventTypes).IsNull()
      .Because("a provider that is not available must not narrow the request to the empty set it reports");
  }

  // ── helpers ─────────────────────────────────────────────────────────────

  private static double _sum(MetricAssertionHelper helper, string instrument, string? kind = null) =>
    helper.GetByName(instrument)
      .Where(m => kind is null || (m.Tags.TryGetValue("kind", out var k) && k == kind))
      .Sum(m => m.Value);

  private static RequestIntegrityManifest _deserializeRequest(IMessageEnvelope envelope) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return (RequestIntegrityManifest)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestIntegrityManifest)))!;
  }

  private static IntegrityAuditWorker _buildWorkerWithOneOrigin(CaptureTransport transport, IEventTypeProvider? typeProvider) {
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(TrackedGuid.New().Value, "origin-a", DateTimeOffset.UtcNow, "origin-a.requests");
    return _buildWorker(new AuditCoordinator(), new CaptureDispatcher(), transport,
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly }, tracker,
      typeProvider: typeProvider, registerTypeProvider: true);
  }

  private static IntegrityAuditWorker _buildWorker(
      AuditCoordinator coordinator, CaptureDispatcher dispatcher, CaptureTransport transport,
      StreamIntegrityOptions options, IntegrityGapTracker? tracker = null,
      StreamIntegrityMetrics? metrics = null,
      ILogger<IntegrityAuditWorker>? logger = null,
      bool registerInstanceProvider = true,
      IEventTypeProvider? typeProvider = null,
      bool registerTypeProvider = false) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton(tracker ?? new IntegrityGapTracker());
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    if (registerInstanceProvider) {
      services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider("auditor-svc"));
    }
    if (registerTypeProvider && typeProvider is not null) {
      services.AddSingleton(typeProvider);
    }
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    return new IntegrityAuditWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      SchemaReadyGate.AlreadyReady(),
      Options.Create(options),
      logger ?? NullLogger<IntegrityAuditWorker>.Instance);
  }

  private sealed class NullValueOptions : IOptions<StreamIntegrityOptions> {
    public StreamIntegrityOptions Value => null!;
  }

  /// <summary>Records rendered log messages so the branch actually taken can be asserted.</summary>
  private sealed class MessageLogger : ILogger<IntegrityAuditWorker> {
    private readonly List<string> _messages = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_messages) { _messages.Add(formatter(state, exception)); }
    }
    public bool Saw(string fragment) {
      lock (_messages) { return _messages.Any(m => m.Contains(fragment, StringComparison.OrdinalIgnoreCase)); }
    }
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
      ProcessId = ProcessId
    };
  }

  private sealed class AuditCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<PerspectiveCoverageGap> Gaps { get; init; } = [];
    public int VerifyCalls { get; private set; }
    public int EpochVerifyCalls { get; private set; }

    public EpochVerificationResult EpochResult { get; init; } = new(0, 0);

    public DigestVerificationResult DigestResult { get; init; } = new() {
      BucketsChecked = 0,
      DriftUpdated = 0,
      DriftRemoved = 0,
      DriftAdded = 0,
    };

    public Task<bool> TryClaimIntegrityAuditCycleAsync(TimeSpan claimWindow, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);

    public Task<long> GetIntegritySealAsync(Guid originServiceId, CancellationToken cancellationToken = default) =>
      Task.FromResult(0L);

    public Task<EpochVerificationResult> VerifyDigestEpochsAsync(
      TimeSpan settleWindow, int maxEpochs, CancellationToken cancellationToken = default) {
      EpochVerifyCalls++;
      return Task.FromResult(EpochResult);
    }

    public Task<IReadOnlyList<PerspectiveCoverageGap>> GetPerspectiveCoverageGapsAsync(
      TimeSpan settleWindow, int maxGaps, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<PerspectiveCoverageGap>>([.. Gaps.Take(maxGaps)]);

    public Task<DigestVerificationResult> VerifyDigestTableAsync(
      TimeSpan settleWindow, CancellationToken cancellationToken = default) {
      VerifyCalls++;
      return Task.FromResult(DigestResult);
    }
  }

  private sealed class CaptureDispatcher : FakeDispatcher, IDispatcher {
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

  private sealed class CaptureTransport : ITransport {
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
