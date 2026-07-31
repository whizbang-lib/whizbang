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
/// Stream-integrity Phase A receptors: the origin answers a manifest request with chunked,
/// targeted digest manifests of its own emissions; the consumer compares a received manifest
/// against its from-that-origin digests — identical folds are silent, divergent buckets report
/// and (ladder-gated) send stream-scoped repair requests back to the origin.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityManifestReceptors.cs</code-under-test>
public class IntegrityManifestReceptorTests {

  [Test]
  public async Task RequestReceptor_SendsChunkedTargetedManifestsAsync() {
    var coordinator = new _auditCoordinator();
    var stream1 = TrackedGuid.NewMedo().Value;
    var stream2 = TrackedGuid.NewMedo().Value;
    var stream3 = TrackedGuid.NewMedo().Value;
    coordinator.OwnDigests = [
      _digest(stream1, 11, 21, 2), _digest(stream2, 12, 22, 1), _digest(stream3, 13, 23, 3),
    ];
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport, new StreamIntegrityOptions { MaxDigestsPerManifest = 2 });
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RequesterService = "auditor-svc",
      Topic = "inbox",
      EventTypes = ["Contracts.TypeX"]
    });

    await Assert.That(transport.Published.Count).IsEqualTo(2)
      .Because("three digests at a chunk bound of two → manifests of 2 + 1.");
    await Assert.That(transport.Published.All(p => p.Envelope.Target == "auditor-svc")).IsTrue()
      .Because("manifests are DIRECTED at the requester — nobody else audits with them.");
    var options = JsonContextRegistry.CreateCombinedOptions();
    var first = (IntegrityManifest)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)transport.Published[0].Envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(IntegrityManifest)))!;
    await Assert.That(first.OriginServiceId).IsEqualTo(coordinator.LocalServiceId);
    await Assert.That(first.Digests.Count).IsEqualTo(2);
  }

  [Test]
  public async Task RequestReceptor_NothingEmitted_StaysSilentAsync() {
    var coordinator = new _auditCoordinator();   // OwnDigests empty
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest { RequesterService = "auditor-svc", Topic = "inbox" });

    await Assert.That(transport.Published).IsEmpty()
      .Because("comparison is per-bucket — absence means nothing to audit, not an empty manifest.");
  }

  [Test]
  public async Task ManifestReceptor_IdenticalFolds_StaySilentAsync() {
    var coordinator = new _auditCoordinator();
    var stream = TrackedGuid.NewMedo().Value;
    coordinator.ReceivedDigests = [_digest(stream, 11, 21, 2)];
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)]));

    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("identical folds prove the bucket complete — silence is the healthy steady state.");
    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task ManifestReceptor_Divergence_ReportsAndCappedRepairsAsync() {
    var coordinator = new _auditCoordinator();
    var mismatched = TrackedGuid.NewMedo().Value;
    var missing = TrackedGuid.NewMedo().Value;
    coordinator.ReceivedDigests = [_digest(mismatched, 99, 21, 1)];   // fold differs; `missing` absent
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped, MaxAutoRepairRequestsPerAudit = 1 },
      dispatcher);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(mismatched, 11, 21, 2), _digest(missing, 12, 22, 3)]));

    var reports = dispatcher.Published.Cast<IntegrityDivergenceDetected>().ToList();
    await Assert.That(reports.Count).IsEqualTo(2)
      .Because("a differing fold AND a missing stream are both divergences worth naming.");
    var missingReport = reports.Single(r => r.AuditedStreamId == missing);
    await Assert.That(missingReport.LocalCount).IsEqualTo(0);
    await Assert.That(missingReport.OriginCount).IsEqualTo(3);

    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("the audit repair cap is a hard budget per manifest chunk.");
    var options = JsonContextRegistry.CreateCombinedOptions();
    var command = (RequestRedeliveryCommand)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)transport.Published[0].Envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestRedeliveryCommand)))!;
    await Assert.That(command.StreamIds!).IsEquivalentTo([mismatched])
      .Because("audit repair is STREAM-scoped — exactly the divergent bucket, nothing broader.");
    await Assert.That(command.EventTypes!).IsEquivalentTo(["Contracts.TypeX"]);
    await Assert.That(transport.Published[0].Envelope.Target).IsEqualTo("origin-svc");
    await Assert.That(command.StateOnly).IsFalse()
      .Because("audit repair is REPAIR semantics — the delivery a live subscriber missed, receptors and all.");
  }

  [Test]
  public async Task Registrar_RegistersBothReceptorsAtThreeStagesAsync() {
    var registry = new _recordingRegistry();
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry>(registry);
    await using var sp = services.BuildServiceProvider();
    var registrar = new IntegrityManifestReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<IntegrityManifestRequestReceptor>.Instance, NullLogger<IntegrityManifestReceptor>.Instance);

    await registrar.StartAsync(CancellationToken.None);

    await Assert.That(registry.Registered.Count).IsEqualTo(6);
    await Assert.That(registry.Registered.Count(r => r.Msg == typeof(RequestIntegrityManifest))).IsEqualTo(3);
    await Assert.That(registry.Registered.Count(r => r.Msg == typeof(IntegrityManifest))).IsEqualTo(3);
  }

  // ── A1c: hierarchical (type-level) exchange + table-driven compares ──────

  [Test]
  public async Task RequestReceptor_TypesLevel_AnswersFromTypeTableAsync() {
    var coordinator = new _auditCoordinator();
    coordinator.OwnTypeDigests = [_typeDigest("Contracts.TypeX", 41, 42, 5)];
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RequesterService = "auditor-svc",
      Topic = "inbox",
      EventTypes = ["Contracts.TypeX"],
      Level = ManifestLevel.Types,
    });

    await Assert.That(transport.Published.Count).IsEqualTo(1);
    var manifest = _deserializeManifest(transport.Published[0].Envelope);
    await Assert.That(manifest.Level).IsEqualTo(ManifestLevel.Types);
    await Assert.That(manifest.Recomputed).IsFalse().Because("Table-driven answers say so — the consumer then compares against ITS table.");
    await Assert.That(manifest.Digests.Count).IsEqualTo(1);
    await Assert.That(manifest.Digests[0].StreamId).IsEqualTo(Guid.Empty);
    await Assert.That(manifest.Digests[0].DigestLo).IsEqualTo(41L);
  }

  [Test]
  public async Task RequestReceptor_TypesLevelWithRecompute_RollsUpComputedRowsAsync() {
    var coordinator = new _auditCoordinator();
    var s1 = TrackedGuid.NewMedo().Value;
    var s2 = TrackedGuid.NewMedo().Value;
    coordinator.OwnDigests = [_digest(s1, 0b1100, 0b0110, 2), _digest(s2, 0b1010, 0b0011, 3)];
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest {
      RequesterService = "auditor-svc",
      Topic = "inbox",
      EventTypes = ["Contracts.TypeX"],
      Level = ManifestLevel.Types,
      UseRecompute = true,
    });

    await Assert.That(transport.Published.Count).IsEqualTo(1);
    var manifest = _deserializeManifest(transport.Published[0].Envelope);
    await Assert.That(manifest.Recomputed).IsTrue().Because("The sweep cycle answers from the event-store recompute.");
    await Assert.That(manifest.Digests.Count).IsEqualTo(1).Because("Two stream rows of one (tenant, type) roll up to one type row.");
    await Assert.That(manifest.Digests[0].DigestLo).IsEqualTo((long)(0b1100 ^ 0b1010))
      .Because("The type digest is the XOR of its stream buckets.");
    await Assert.That(manifest.Digests[0].EventCount).IsEqualTo(5);
  }

  [Test]
  public async Task ManifestReceptor_TypeLevelMatch_NoDrillDownAsync() {
    var coordinator = new _auditCoordinator();
    coordinator.ReceivedTypeDigests = [_typeDigest("Contracts.TypeX", 41, 42, 5)];
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)], ManifestLevel.Types));

    await Assert.That(transport.Published).IsEmpty()
      .Because("Matching type roll-ups prove every stream bucket of the type complete — no drill-down.");
    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("Reports only ever come from the stream-level compare.");
  }

  [Test]
  public async Task ManifestReceptor_TypeLevelMismatch_SendsCappedDrillDownAsync() {
    var coordinator = new _auditCoordinator();
    coordinator.ReceivedTypeDigests = [_typeDigest("Contracts.TypeX", 99, 42, 4)];   // differs; TypeY missing
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { MaxDrillDownTypesPerAudit = 1 }, dispatcher);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [
      _typeDigest("Contracts.TypeX", 41, 42, 5),
      _typeDigest("Contracts.TypeY", 51, 52, 2),
    ], ManifestLevel.Types));

    await Assert.That(transport.Published.Count).IsEqualTo(1)
      .Because("Two mismatched types at a drill-down cap of one → exactly one escalation this cycle.");
    var request = _deserializeRequest(transport.Published[0].Envelope);
    await Assert.That(request.Level).IsEqualTo(ManifestLevel.Streams)
      .Because("The drill-down asks for stream granularity of the mismatched types only.");
    await Assert.That(request.EventTypes!.Count).IsEqualTo(1);
    await Assert.That(request.UseRecompute).IsFalse().Because("Table-driven cycles drill down table-driven.");
    await Assert.That(transport.Published[0].Envelope.Target).IsEqualTo("origin-svc");
    await Assert.That(dispatcher.Published).IsEmpty().Because("Type-level mismatches escalate; they never report directly.");
  }

  [Test]
  public async Task ManifestReceptor_TypeLevel_SettleSkipsFreshBucketsAsync() {
    var coordinator = new _auditCoordinator();
    coordinator.ReceivedTypeDigests = [_typeDigest("Contracts.TypeX", 99, 42, 4)];   // differs...
    var transport = new _captureTransport();
    var sp = _provider(coordinator, transport);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    // ...but the origin's bucket changed moments ago — in-flight deliveries, not divergence.
    await receptor.HandleAsync(_manifest(coordinator, [
      _typeDigest("Contracts.TypeX", 41, 42, 5) with { UpdatedAt = DateTimeOffset.UtcNow },
    ], ManifestLevel.Types));

    await Assert.That(transport.Published).IsEmpty()
      .Because("A bucket updated inside the settle window is skipped — the incremental equivalent of the recompute's settle filter.");
  }

  [Test]
  public async Task ManifestReceptor_StreamLevel_TableMode_ComparesAgainstTableAsync() {
    var coordinator = new _auditCoordinator();
    var stream = TrackedGuid.NewMedo().Value;
    // The TABLE lane disagrees with the origin; the recompute lane would agree — the report
    // proves table-driven manifests compare against the consumer's TABLE.
    coordinator.ReceivedTableDigests = [_digest(stream, 99, 21, 1)];
    coordinator.ReceivedDigests = [_digest(stream, 11, 21, 2)];
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)]));

    var reports = dispatcher.Published.Cast<IntegrityDivergenceDetected>().ToList();
    await Assert.That(reports.Count).IsEqualTo(1)
      .Because("Non-recomputed manifests compare against the maintained table, not a fresh recompute.");
  }

  [Test]
  public async Task ManifestReceptor_StreamLevel_RecomputedManifest_ComparesAgainstRecomputeAsync() {
    var coordinator = new _auditCoordinator();
    var stream = TrackedGuid.NewMedo().Value;
    // Inverse of the table-mode test: recompute agrees, table disagrees — a sweep manifest
    // (Recomputed=true) must stay silent because it compares against the consumer's recompute.
    coordinator.ReceivedTableDigests = [_digest(stream, 99, 21, 1)];
    coordinator.ReceivedDigests = [_digest(stream, 11, 21, 2)];
    var transport = new _captureTransport();
    var dispatcher = new _captureDispatcher();
    var sp = _provider(coordinator, transport, dispatcher: dispatcher);
    var receptor = new IntegrityManifestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 11, 21, 2)], recomputed: true));

    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("Sweep manifests compare recompute-to-recompute end to end.");
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static StreamDigest _digest(Guid stream, long lo, long hi, int count) => new() {
    TenantScope = "tenant-a",
    EventType = "Contracts.TypeX",
    StreamId = stream,
    DigestLo = lo,
    DigestHi = hi,
    EventCount = count,
  };

  private static StreamDigest _typeDigest(string eventType, long lo, long hi, int count) => new() {
    TenantScope = "tenant-a",
    EventType = eventType,
    StreamId = Guid.Empty,
    DigestLo = lo,
    DigestHi = hi,
    EventCount = count,
  };

  private static IntegrityManifest _manifest(
      _auditCoordinator coordinator, List<StreamDigest> digests,
      ManifestLevel level = ManifestLevel.Streams, bool recomputed = false) => new() {
        ManifestStreamId = coordinator.OriginId,
        OriginServiceId = coordinator.OriginId,
        OriginServiceName = "origin-svc",
        Digests = digests,
        Level = level,
        Recomputed = recomputed,
      };

  private static IntegrityManifest _deserializeManifest(IMessageEnvelope envelope) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return (IntegrityManifest)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(IntegrityManifest)))!;
  }

  private static RequestIntegrityManifest _deserializeRequest(IMessageEnvelope envelope) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return (RequestIntegrityManifest)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestIntegrityManifest)))!;
  }

  private static ServiceProvider _provider(
      _auditCoordinator coordinator, _captureTransport transport,
      StreamIntegrityOptions? options = null, _captureDispatcher? dispatcher = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IDispatcher>(dispatcher ?? new _captureDispatcher());
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider("auditor-svc"));
    services.AddSingleton(Options.Create(options ?? new StreamIntegrityOptions()));
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    return services.BuildServiceProvider();
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

  private sealed class _auditCoordinator : IWorkCoordinator {
    public Guid LocalServiceId { get; } = TrackedGuid.NewMedo().Value;
    public Guid OriginId { get; } = TrackedGuid.NewMedo().Value;
    public IReadOnlyList<StreamDigest> OwnDigests { get; set; } = [];
    public IReadOnlyList<StreamDigest> ReceivedDigests { get; set; } = [];
    public IReadOnlyList<StreamDigest> OwnTableDigests { get; set; } = [];
    public IReadOnlyList<StreamDigest> ReceivedTableDigests { get; set; } = [];
    public IReadOnlyList<StreamDigest> OwnTypeDigests { get; set; } = [];
    public IReadOnlyList<StreamDigest> ReceivedTypeDigests { get; set; } = [];

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(LocalServiceId);

    public Task<IReadOnlyList<StreamDigest>> ComputeStreamDigestsAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes, TimeSpan settleWindow, CancellationToken cancellationToken = default) =>
      Task.FromResult(originServiceId is null ? OwnDigests : ReceivedDigests);

    public Task<IReadOnlyList<StreamDigest>> GetStreamDigestsAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(originServiceId is null ? OwnTableDigests : ReceivedTableDigests);

    public Task<IReadOnlyList<StreamDigest>> GetTypeDigestsAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(originServiceId is null ? OwnTypeDigests : ReceivedTypeDigests);

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
    public Task RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken ct = default) => Task.CompletedTask;
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

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, Whizbang.Core.Dispatch.DispatchOptions options) => PublishAsync(eventData);
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, Whizbang.Core.Dispatch.DispatchOptions options) where TMessage : notnull => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, Whizbang.Core.Dispatch.DispatchOptions options) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, Whizbang.Core.Dispatch.DispatchOptions options, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, Whizbang.Core.Dispatch.DispatchOptions options) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message, Whizbang.Core.Dispatch.DispatchOptions options) => throw new NotSupportedException();
    public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message) => throw new NotSupportedException();
    public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, Whizbang.Core.Dispatch.DispatchOptions options) => throw new NotSupportedException();
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CascadeMessageAsync(IMessage message, Whizbang.Core.Dispatch.DispatchModes mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, Whizbang.Core.Dispatch.DispatchModes mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
