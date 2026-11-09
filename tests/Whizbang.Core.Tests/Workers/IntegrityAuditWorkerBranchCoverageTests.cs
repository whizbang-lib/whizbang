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
  public async Task RunAuditOnce_ConsumerWithNoPerspectives_RequestsOnlyTheEventItsReceptorConsumesAsync() {
    var transport = new CaptureTransport();
    // The application knows A and B (a shared contracts assembly) but only a receptor for A is registered, and no
    // perspective: the perspective event-type provider is unavailable.
    var worker = _buildWorkerWithOneOrigin(transport, typeProvider: NullEventTypeProvider.Instance,
      consumed: [_event(TYPE_A), new ConsumedMessageInfo(COMMAND, IsEvent: false)]);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    var request = _deserializeRequest(transport.Published.Single().Envelope);
    await Assert.That(request.EventTypes).IsEquivalentTo([TYPE_A])
      .Because("the audit asks for what this service consumes, never for every type (null) and never for B, which nothing here consumes");
  }

  [Test]
  public async Task RunAuditOnce_PerspectiveAndReceptorConsumer_RequestIncludesTheReceptorOnlyEventAsync() {
    var transport = new CaptureTransport();
    var worker = _buildWorkerWithOneOrigin(transport, typeProvider: new PerspectiveTypes(),
      consumed: [_event(TYPE_A), _event(TYPE_R)]);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    var request = _deserializeRequest(transport.Published.Single().Envelope);
    await Assert.That(request.EventTypes).IsEquivalentTo([TYPE_A, TYPE_R])
      .Because("an event only a receptor consumes is still stored here, so it is audited like a perspective's");
  }

  [Test]
  public async Task RunAuditOnce_ConsumesNoEvents_RequestsNothingAsync() {
    var transport = new CaptureTransport();
    var coordinator = new AuditCoordinator();
    var worker = _buildWorkerWithOneOrigin(transport, typeProvider: null,
      consumed: [new ConsumedMessageInfo(COMMAND, IsEvent: false)], coordinator: coordinator);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(transport.Published).IsEmpty()
      .Because("a service that consumes no events has nothing to audit; an empty filter must never widen to 'every type'");
    await Assert.That(coordinator.ForgetCalls.Single()).IsEmpty()
      .Because("every ledger row of such a service is for a type it does not consume");
  }

  [Test]
  public async Task RunAuditOnce_ForgetsTheLedgerRowsOfEveryTypeOutsideTheConsumedSetAsync() {
    var coordinator = new AuditCoordinator();
    var worker = _buildWorkerWithOneOrigin(new CaptureTransport(), typeProvider: null,
      consumed: [_event(TYPE_A)], coordinator: coordinator);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.ForgetCalls.Single()).IsEquivalentTo([TYPE_A])
      .Because("the audit hands the ledger the consumed set each cycle, so rows recorded for other types go away");
  }

  [Test]
  public async Task RunAuditOnce_AbsorbedNamespace_WithNothingConsumingIt_IsStillAuditedAsync() {
    var transport = new CaptureTransport();
    var coordinator = new AuditCoordinator();
    var worker = _buildWorkerWithOneOrigin(transport, typeProvider: null,
      consumed: [new ConsumedMessageInfo(COMMAND, IsEvent: false)], coordinator: coordinator,
      absorbedNamespaces: ["Contracts.Jobs"]);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    var request = _deserializeRequest(transport.Published.Single().Envelope);
    await Assert.That(request.EventNamespaces).IsEquivalentTo(["contracts.jobs"])
      .Because("an absorbed namespace's events are stored here on purpose, so they are audited though nothing consumes them");
    await Assert.That(request.EventTypes).IsEmpty()
      .Because("the type list stays the consumed events: empty, never null (which an origin reads as every type)");
    await Assert.That(coordinator.ForgetNamespaces.Single()).IsEquivalentTo(["contracts.jobs"])
      .Because("the ledger keeps the rows of an absorbed namespace's types");
  }

  [Test]
  public async Task RunAuditOnce_ForgottenLedgerRows_AreLoggedAsync() {
    var coordinator = new AuditCoordinator { ForgetResult = 3 };
    var logger = new MessageLogger();
    var tracker = new IntegrityGapTracker();
    var worker = _buildWorker(coordinator, new CaptureDispatcher(), new CaptureTransport(),
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly }, tracker, logger: logger);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(logger.Saw("forgot 3 repair-ledger row(s)")).IsTrue()
      .Because("rows removed from the ledger change the unhealed gauge, so the audit says why it moved");
  }

  // ── a ledger row filed under an origin that does not own its type ─────

  private static readonly Guid _ownerSvc = Guid.Parse("0a000000-0000-0000-0000-0000000000a1");
  private static readonly Guid _copySvc = Guid.Parse("0b000000-0000-0000-0000-0000000000b2");
  private const string ORDERS_TYPE = "App.Contracts.Orders.Placed, App.Contracts";
  private const string OWN_TYPE = "App.Contracts.Self.Started, App.Contracts";
  private const string UNCLAIMED_TYPE = "App.Contracts.Loose.Happened, App.Contracts";

  [Test]
  public async Task RunAuditOnce_LedgerRowsOfAnOriginThatDoesNotOwnTheType_AreForgottenAsync() {
    var coordinator = new AuditCoordinator {
      Claims = [new IntegrityDomainClaim { Domain = "app.contracts.orders", OwnerServiceId = _ownerSvc, OwnerServiceName = "orders-svc" }],
      LedgerPairs = [
        (_copySvc, ORDERS_TYPE),     // a copy-holder's report of the owner's type
        (_ownerSvc, ORDERS_TYPE),    // the owner's own report
        (_copySvc, OWN_TYPE),        // another origin's report of this service's own type
        (_copySvc, UNCLAIMED_TYPE),  // nobody claims the type: the origin is taken at its word
      ],
    };
    var logger = new MessageLogger();
    var worker = _buildWorker(coordinator, new CaptureDispatcher(), new CaptureTransport(),
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped }, logger: logger,
      ownedDomains: ["App.Contracts.Self"]);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.ForgottenPairs).IsEquivalentTo([(_copySvc, ORDERS_TYPE), (_copySvc, OWN_TYPE)])
      .Because("a non-owner's answer leaves out every type it does not own, so its rows are never compared again and "
             + "can never heal or clear; they are false differences, and the owner's own rows stay");
    await Assert.That(logger.Saw("forgot 2 repair-ledger row(s) recorded under an origin that does not own")).IsTrue()
      .Because("rows removed from the ledger move the unhealed gauge, so the audit says why");
  }

  [Test]
  public async Task RunAuditOnce_WithNoNonOwnerRows_LogsNothingAboutThemAsync() {
    var coordinator = new AuditCoordinator { LedgerPairs = [(_copySvc, UNCLAIMED_TYPE)] };
    var logger = new MessageLogger();
    var worker = _buildWorker(coordinator, new CaptureDispatcher(), new CaptureTransport(),
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly }, logger: logger);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.ForgottenPairs).IsEmpty()
      .Because("with no owner known and no domain of its own, this service takes every origin at its word, as before");
    await Assert.That(logger.Saw("does not own")).IsFalse();
  }

  [Test]
  public async Task RunAuditOnce_TheManifestRequest_ExpiresWithTheIntegrityTimeToLiveAsync() {
    var transport = new CaptureTransport();
    var worker = _buildWorkerWithOneOrigin(transport, typeProvider: null, consumed: [_event(TYPE_A)]);

    await worker.RunAuditOnceAsync(CancellationToken.None);

    var destination = transport.Published.Single().Destination;
    await Assert.That(destination.Address).IsEqualTo("origin-a.requests");
    await Assert.That(Whizbang.Core.Transports.ControlMessageTtl.FromMetadata(destination.Metadata)).IsEqualTo(TimeSpan.FromMinutes(60))
      .Because("#2078: a manifest request is regenerated every cycle, so a stale one must expire at the broker");
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

  private const string TYPE_A = "Contracts.OrderShipped, Contracts";
  private const string TYPE_R = "Contracts.OrderAudited, Contracts";
  private const string COMMAND = "Contracts.ShipOrder, Contracts";

  private static ConsumedMessageInfo _event(string typeName) => new(typeName, IsEvent: true);

  private static IntegrityAuditWorker _buildWorkerWithOneOrigin(
      CaptureTransport transport, IEventTypeProvider? typeProvider, ConsumedMessageInfo[] consumed,
      AuditCoordinator? coordinator = null, string[]? absorbedNamespaces = null) {
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(TrackedGuid.New().Value, "origin-a", DateTimeOffset.UtcNow, "origin-a.requests");
    return _buildWorker(coordinator ?? new AuditCoordinator(), new CaptureDispatcher(), transport,
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly }, tracker,
      typeProvider: typeProvider, registerTypeProvider: true, consumed: consumed, absorbedNamespaces: absorbedNamespaces);
  }

  private static IntegrityAuditWorker _buildWorker(
      AuditCoordinator coordinator, CaptureDispatcher dispatcher, CaptureTransport transport,
      StreamIntegrityOptions options, IntegrityGapTracker? tracker = null,
      StreamIntegrityMetrics? metrics = null,
      ILogger<IntegrityAuditWorker>? logger = null,
      bool registerInstanceProvider = true,
      IEventTypeProvider? typeProvider = null,
      bool registerTypeProvider = false,
      ConsumedMessageInfo[]? consumed = null,
      string[]? absorbedNamespaces = null,
      string[]? ownedDomains = null) {
    var services = new ServiceCollection();
    if (absorbedNamespaces is not null || ownedDomains is not null) {
      services.AddSingleton(Options.Create(new Whizbang.Core.Routing.RoutingOptions()
        .AbsorbNamespaces(absorbedNamespaces ?? []).OwnDomains(ownedDomains ?? [])));
    }
    services.AddSingleton<IConsumerRegistry>(new ConsumedRegistry(consumed ?? [_event(TYPE_A)]));
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
      Options.Create(options),
      logger ?? NullLogger<IntegrityAuditWorker>.Instance);
  }

  /// <summary>The consumer registry: what this service consumes.</summary>
  private sealed class ConsumedRegistry(ConsumedMessageInfo[] consumed) : IConsumerRegistry {
    public bool HasAnyConsumer(string messageType) => consumed.Any(c => c.TypeName == messageType);
    public IReadOnlyList<ConsumedMessageInfo> GetConsumedMessages() => consumed;
  }

  /// <summary>A perspective event-type provider that knows A only (the perspective folds A).</summary>
  private sealed class PerspectiveTypes : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(PerspectiveFoldedEvent)];
  }

  private sealed record PerspectiveFoldedEvent : IEvent;

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
    public List<IReadOnlyList<string>> ForgetCalls { get; } = [];
    public int ForgetResult { get; init; }
    public List<IReadOnlyCollection<string>> ForgetNamespaces { get; } = [];

    public Task<int> IntegrityForgetUnconsumedEventTypesAsync(
        IReadOnlyList<string> consumedEventTypes, IReadOnlyCollection<string> absorbedNamespaces,
        CancellationToken cancellationToken = default) {
      ForgetCalls.Add(consumedEventTypes);
      ForgetNamespaces.Add(absorbedNamespaces);
      return Task.FromResult(ForgetResult);
    }

    public IReadOnlyList<IntegrityDomainClaim> Claims { get; init; } = [];
    public List<(Guid Origin, string EventType)> LedgerPairs { get; init; } = [];
    public List<(Guid Origin, string EventType)> ForgottenPairs { get; } = [];

    public Task<IReadOnlyList<IntegrityDomainClaim>> GetIntegrityDomainClaimsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(Claims);

    public Task<int> IntegrityForgetNonOwnerLedgerRowsAsync(
        Func<Guid, string, bool> isNotAuthoritative, CancellationToken cancellationToken = default) {
      var gone = LedgerPairs.Where(p => isNotAuthoritative(p.Origin, p.EventType)).ToList();
      ForgottenPairs.AddRange(gone);
      return Task.FromResult(gone.Count);
    }

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
