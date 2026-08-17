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
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Unit tests (no Postgres) for the stream-integrity Phase B consumer bridge:
/// <see cref="IntegrityCheckpointReceptor"/> verifies a received checkpoint against local receipt
/// counts. Deficits are PENDING on first sight; a deficit persisting past the origin's NEXT
/// checkpoint CONFIRMS (report event; ladder-gated wire-only targeted repair request); healed
/// deficits clear silently; own checkpoints and unsubscribed types are ignored.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityCheckpointReceptor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityGapTracker.cs</code-under-test>
public class IntegrityCheckpointReceptorTests {

  public sealed record VerifiedEvent : IEvent {
    [StreamId]
    public Guid Sid { get; init; }
  }

  // The wire form ("Type, Assembly") — checkpoint buckets are built from wh_event_store.event_type,
  // so the subscribed-type filter must match THAT form or fresh-window verification silently skips
  // every bucket.
  private static readonly string _verifiedType = TypeNameFormatter.Format(typeof(VerifiedEvent));

  [Test]
  public async Task FirstDeficit_IsPendingOnly_NoReportAsync() {
    var fx = _fixture();
    fx.Coordinator.Counts = _ => [];   // consumer has NOTHING in the window

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));

    await Assert.That(fx.Dispatcher.Published).IsEmpty()
      .Because("a first-sight deficit is PENDING — two-cycle confirmation absorbs in-flight stragglers.");
    await Assert.That(fx.Transport.Published).IsEmpty();
  }

  [Test]
  public async Task ManyConfirmedGaps_ReportsAreCapped_WithOneSummaryAsync() {
    // Found by scripts/Lint-UnboundedFanOut.ps1, not by review: MaxAutoRepairRequestsPerCheckpoint
    // caps the REPAIRS in this loop while the IntegrityGapDetected publish next to it runs free.
    // That is the same asymmetry that took the fleet down through the manifest comparator -- each
    // report is a durable outbox write, so one per confirmed gap is unbounded sequential I/O
    // inside a single handler, and pendings grow with (tenant x event type), not with a batch size.
    const int cap = 5;
    var fx = _fixture(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.ReportOnly,
      MaxGapReportsPerCheckpoint = cap,
      PublishReportEvents = true,
    });
    // Every bucket stays short on the recount, so every pending confirms as a real gap.
    fx.Coordinator.Counts = _ => [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = 0 }];

    var tenants = Enumerable.Range(1, 60).Select(i => $"tenant-{i}").ToList();
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3, tenantScopes: tenants));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true));

    var reports = fx.Dispatcher.Published.OfType<IntegrityGapDetected>().ToList();
    await Assert.That(reports.Count).IsLessThanOrEqualTo(cap)
      .Because("each gap report is a durable write; an unbounded fan-out starves the pipeline.");
    await Assert.That(reports.Count).IsGreaterThan(0)
      .Because("capping must not silence gaps entirely — the first ones still have to be named.");
  }

  [Test]
  public async Task DeficitPersistingPastNextCheckpoint_ConfirmsAndReportsAsync() {
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.ReportOnly, PublishReportEvents = true });
    fx.Coordinator.Counts = _ => [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = 1 }];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));   // deficit → pending
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true));   // next cycle

    var report = (IntegrityGapDetected)fx.Dispatcher.Published.Single();
    await Assert.That(report.EventType).IsEqualTo(_verifiedType);
    await Assert.That(report.TenantScope).IsEqualTo("tenant-a");
    await Assert.That(report.ExpectedCount).IsEqualTo(3);
    await Assert.That(report.ActualCount).IsEqualTo(1)
      .Because("the report carries the recount at confirmation time, not the original sighting.");
    await Assert.That(report.OriginServiceId).IsEqualTo(fx.OriginId);
    await Assert.That(report.AutoRepairRequested).IsFalse()
      .Because("the ReportOnly rung reports without repairing — the operator's explicit opt-down.");
    await Assert.That(fx.Transport.Published).IsEmpty();
  }

  [Test]
  public async Task DeficitHealedBeforeNextCheckpoint_ClearsSilentlyAsync() {
    var fx = _fixture();
    var calls = 0;
    fx.Coordinator.Counts = _ => ++calls == 1
      ? []   // first sight: nothing received yet
      : [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = 3 }];   // straggler landed

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true));

    await Assert.That(fx.Dispatcher.Published).IsEmpty()
      .Because("a deficit that heals before the next checkpoint was an in-flight straggler, not a gap.");
  }

  /// <summary>
  /// Sibling of ManifestReceptor_ByDefault_DetectsAndRepairsButPublishesNoReports, for the
  /// checkpoint path: a confirmed gap is still detected and still repaired, but publishes no
  /// durable <see cref="IntegrityGapDetected"/> unless the operator opts in.
  /// </summary>
  [Test]
  public async Task ByDefault_ConfirmedGap_RepairsWithoutPublishingAReportAsync() {
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped });
    fx.Coordinator.Counts = _ => [];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 10, to: 20, count: 4, requestTopic: "origin.requests"));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 20, to: 20, count: 0, emptyBuckets: true, requestTopic: "origin.requests"));

    await Assert.That(fx.Dispatcher.Published.OfType<IntegrityGapDetected>().Any()).IsFalse()
      .Because("the gap is real and recorded; publishing an event nobody consumes is not how it "
               + "gets recorded");
    await Assert.That(fx.Transport.Published.Count).IsEqualTo(1)
      .Because("the repair request is the half of this that actually fixes the deficit");
  }

  [Test]
  public async Task AutoRepairCapped_SendsTargetedWireOnlyRepairRequestAsync() {
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, PublishReportEvents = true });
    fx.Coordinator.Counts = _ => [];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 10, to: 20, count: 4, requestTopic: "origin.requests"));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 20, to: 20, count: 0, emptyBuckets: true, requestTopic: "origin.requests"));

    var report = (IntegrityGapDetected)fx.Dispatcher.Published.Single();
    await Assert.That(report.AutoRepairRequested).IsTrue();

    var (envelope, destination, _) = fx.Transport.Published.Single();
    await Assert.That(destination.Address).IsEqualTo("origin.requests")
      .Because("the request publishes to the ORIGIN-carried address — never to the requester's own topic.");
    await Assert.That(envelope.Target).IsEqualTo("origin-svc")
      .Because("the request is DIRECTED at the origin — only it should run the selection.");
    var options = JsonContextRegistry.CreateCombinedOptions();
    var command = (RequestRedeliveryCommand)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestRedeliveryCommand)))!;
    await Assert.That(command.FromCommitSequence).IsEqualTo(10L);
    await Assert.That(command.ToCommitSequence).IsEqualTo(20L)
      .Because("the repair is scoped to EXACTLY the confirmed window.");
    await Assert.That(command.EventTypes!).IsEquivalentTo([_verifiedType]);
    await Assert.That(command.TenantScope).IsEqualTo("tenant-a");
    await Assert.That(command.RequesterService).IsEqualTo("consumer-svc")
      .Because("the requester names itself — it becomes the returned bundles' Target.");
    await Assert.That(command.Topic).IsEqualTo("inbox");
  }

  [Test]
  public async Task ConfirmedGap_NoOriginRequestTopic_WithholdsRepairRequestAsync() {
    var fx = _fixture(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, PublishReportEvents = true });
    fx.Coordinator.Counts = _ => [];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 10, to: 20, count: 4));
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 20, to: 20, count: 0, emptyBuckets: true));

    await Assert.That(fx.Dispatcher.Published.Cast<IntegrityGapDetected>().Single().ExpectedCount).IsEqualTo(4)
      .Because("the confirmed gap still reports — only the misroutable request is withheld.");
    await Assert.That(fx.Transport.Published).IsEmpty()
      .Because("an origin that never announced a request address cannot be asked without " +
               "broadcasting off the requester's own topic — the observed all-to-all flood. " +
               "The origin's next checkpoint carries the address; the gap re-confirms then.");
  }

  [Test]
  public async Task OwnCheckpoint_IsIgnoredAsync() {
    var fx = _fixture();
    fx.Coordinator.Counts = _ => [];

    var own = new IntegrityCheckpoint {
      CheckpointStreamId = fx.Coordinator.LocalServiceId,
      OriginServiceId = fx.Coordinator.LocalServiceId,
      OriginServiceName = "consumer-svc",
      FromCommitSequence = 0,
      ToCommitSequence = 5,
      Buckets = [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = 3 }],
    };
    await fx.Receptor.HandleAsync(own);
    await fx.Receptor.HandleAsync(own with { FromCommitSequence = 5 });

    await Assert.That(fx.Dispatcher.Published).IsEmpty()
      .Because("locally-originated events persist no origin stamp — a self-count would always " +
               "read zero and every bucket would false-alarm.");
  }

  [Test]
  public async Task UnsubscribedType_IsNotVerifiedAsync() {
    var fx = _fixture();
    fx.Coordinator.Counts = _ => [];

    var checkpoint = new IntegrityCheckpoint {
      CheckpointStreamId = fx.OriginId,
      OriginServiceId = fx.OriginId,
      OriginServiceName = "origin-svc",
      FromCommitSequence = 0,
      ToCommitSequence = 5,
      Buckets = [new CheckpointBucket { TenantScope = "tenant-a", EventType = "Contracts.NotMine", Count = 7 }],
    };
    await fx.Receptor.HandleAsync(checkpoint);
    await fx.Receptor.HandleAsync(checkpoint with { FromCommitSequence = 5, Buckets = [] });

    await Assert.That(fx.Dispatcher.Published).IsEmpty()
      .Because("a type this consumer does not subscribe to is someone else's contract with the origin.");
  }

  [Test]
  public async Task Registrar_RegistersReceptorAtThreeDefaultStagesAsync() {
    var registry = new _recordingRegistry();
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry>(registry);
    await using var sp = services.BuildServiceProvider();
    var registrar = new IntegrityCheckpointReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityCheckpointReceptor>.Instance);

    await registrar.StartAsync(CancellationToken.None);

    await Assert.That(registry.Registered.Count).IsEqualTo(3);
    await Assert.That(registry.Registered.All(r => r.Msg == typeof(IntegrityCheckpoint))).IsTrue();
  }

  // ── fixture ─────────────────────────────────────────────────────────────

  private sealed class _fixtureState {
    public required _verifyCoordinator Coordinator { get; init; }
    public required _captureDispatcher Dispatcher { get; init; }
    public required _captureTransport Transport { get; init; }
    public required IntegrityCheckpointReceptor Receptor { get; init; }
    public Guid OriginId { get; } = TrackedGuid.NewMedo().Value;
  }

  private static _fixtureState _fixture(
      StreamIntegrityOptions? options = null,
      Whizbang.Core.Observability.StreamIntegrityMetrics? metrics = null) {
    var coordinator = new _verifyCoordinator();
    var dispatcher = new _captureDispatcher();
    var transport = new _captureTransport();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton(metrics
      ?? new Whizbang.Core.Observability.StreamIntegrityMetrics(new Whizbang.Core.Observability.WhizbangMetrics()));
    services.AddSingleton(new IntegrityGapTracker());
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IEventTypeProvider>(new _typeProvider());
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider("consumer-svc"));
    // See IntegrityManifestReceptorTests: publishing is opt-in; these exercise that path.
    services.AddSingleton(Options.Create(options ?? new StreamIntegrityOptions { PublishReportEvents = true }));
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    return new _fixtureState {
      Coordinator = coordinator,
      Dispatcher = dispatcher,
      Transport = transport,
      Receptor = new IntegrityCheckpointReceptor(
        sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityCheckpointReceptor>.Instance),
    };
  }

  [Test]
  public async Task ConfirmedGap_WithDefaultOptions_EmitsGapAndRepairCountersAsync() {
    // Filter on THIS test's meter INSTANCE (not the name) — parallel tests share the meter name.
    var metrics = new Whizbang.Core.Observability.StreamIntegrityMetrics(new Whizbang.Core.Observability.WhizbangMetrics());
    var meter = metrics.GapsDetected.Meter;
    var measurements = new Dictionary<string, long>();
    using var listener = new System.Diagnostics.Metrics.MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument.Meter, meter)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => {
      lock (measurements) {
        measurements[instrument.Name] = measurements.GetValueOrDefault(instrument.Name) + value;
      }
    });
    listener.Start();

    var fx = _fixture(metrics: metrics);   // DEFAULT options — the self-healing posture
    fx.Coordinator.Counts = _ => [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = 1 }];

    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 0, to: 5, count: 3));   // deficit → pending
    await fx.Receptor.HandleAsync(_checkpoint(fx, from: 5, to: 5, count: 0, emptyBuckets: true));   // confirms

    await Assert.That(measurements.GetValueOrDefault("whizbang.stream_integrity.checkpoints_received")).IsEqualTo(2L);
    await Assert.That(measurements.GetValueOrDefault("whizbang.stream_integrity.gaps_detected")).IsEqualTo(1L)
      .Because("the confirmed gap must be countable — sustained non-zero is the operator's alarm.");
    await Assert.That(measurements.GetValueOrDefault("whizbang.stream_integrity.repairs_requested")).IsEqualTo(1L)
      .Because("the DEFAULT posture auto-repairs; the counter proves the healer acted, not just detected.");
  }

  private static IntegrityCheckpoint _checkpoint(
      _fixtureState fx, long from, long to, int count, bool emptyBuckets = false, string? requestTopic = null,
      IReadOnlyList<string>? tenantScopes = null) => new() {
        CheckpointStreamId = fx.OriginId,
        OriginServiceId = fx.OriginId,
        OriginServiceName = "origin-svc",
        RequestTopic = requestTopic,
        FromCommitSequence = from,
        ToCommitSequence = to,
        Buckets = emptyBuckets
      ? []
      : tenantScopes is not null
        // Pendings are keyed by (tenant, event type), so a multi-tenant window is how the confirmed-gap
        // count grows past anything a batch size bounds — the shape that has to stay capped.
        ? [.. tenantScopes.Select(t => new CheckpointBucket { TenantScope = t, EventType = _verifiedType, Count = count })]
        : [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = count }],
      };

  // ── fakes ───────────────────────────────────────────────────────────────

  private sealed class _typeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(VerifiedEvent)];
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

  private sealed class _verifyCoordinator : IWorkCoordinator {
    public Guid LocalServiceId { get; } = TrackedGuid.NewMedo().Value;
    public Func<(Guid Origin, long From, long To), IReadOnlyList<CheckpointBucket>> Counts { get; set; } = _ => [];

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(LocalServiceId);

    public Task<IReadOnlyList<CheckpointBucket>> CountReceivedFromOriginAsync(
      Guid originServiceId, long fromCommitSequence, long toCommitSequence, CancellationToken cancellationToken = default) =>
      Task.FromResult(Counts((originServiceId, fromCommitSequence, toCommitSequence)));

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken ct = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken ct = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken ct = default) => Task.FromResult(true);
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

  private sealed class _recordingRegistry : IReceptorRegistry {
    public List<(Type Msg, LifecycleStage Stage)> Registered { get; } = [];
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
      Registered.Add((typeof(TMessage), stage));
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  /// <summary>Captures PublishAsync payloads; every other dispatcher member is unused here.</summary>
  private sealed class _captureDispatcher : IDispatcher {
    public List<object> Published { get; } = [];

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
      Published.Add(eventData!);
      return Task.FromResult<IDeliveryReceipt>(new _receipt());
    }

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options) => PublishAsync(eventData);
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, DispatchOptions options) where TMessage : notnull => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, DispatchOptions options) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, DispatchOptions options, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, DispatchOptions options) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message, DispatchOptions options) => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message) => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, DispatchOptions options) => throw new NotSupportedException();
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CascadeMessageAsync(IMessage message, DispatchModes mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, DispatchModes mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) => throw new NotSupportedException();
    public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) => throw new NotSupportedException();
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages) => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events) => throw new NotSupportedException();

    private sealed class _receipt : IDeliveryReceipt {
      public MessageId MessageId => MessageId.New();
      public CorrelationId? CorrelationId => null;
      public MessageId? CausationId => null;
      public DateTimeOffset Timestamp => DateTimeOffset.UtcNow;
      public string Destination => "test";
      public DeliveryStatus Status => DeliveryStatus.Delivered;
      public IReadOnlyDictionary<string, JsonElement> Metadata => new Dictionary<string, JsonElement>();
      public Guid? StreamId => null;
    }
  }
}
