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
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Priority;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Priority step 1, background work: every control message the integrity receptors publish straight to the
/// transport (a manifest answer, a drill-down or cursor follow-up, a repair request, a bulk backfill request)
/// declares <see cref="WorkPriority.BACKGROUND"/> on its envelope. Reconciliation is served from the other
/// side's inbox; at the standard number it would be claimed ahead of that service's live standard work.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#background-work</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityCheckpointReceptor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityManifestReceptors.cs</code-under-test>
[Category("Shard4")]
public class IntegrityReceptorsPriorityTests {
  private static readonly string _verifiedType = TypeNameFormatter.Format(typeof(IntegrityCheckpointReceptorTests.VerifiedEvent));

  // ── IntegrityCheckpointReceptor ─────────────────────────────────────────

  [Test]
  public async Task CheckpointReceptor_ConfirmedGap_TheRepairRequestIsBackgroundAsync() {
    var coordinator = new _verifyCoordinator();
    var transport = new _captureTransport();
    var sp = _checkpointProvider(coordinator, transport, new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped });
    var receptor = new IntegrityCheckpointReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityCheckpointReceptor>.Instance);
    var originId = TrackedGuid.NewMedo().Value;

    await receptor.HandleAsync(_checkpoint(originId, from: 10, to: 20, count: 4));
    await receptor.HandleAsync(_checkpoint(originId, from: 20, to: 20, count: 0, emptyBuckets: true));

    var (envelope, _, envelopeType) = transport.Published.Single();
    await Assert.That(envelopeType).Contains(nameof(RequestRedeliveryCommand));
    await Assert.That(envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the origin serves the repair from its own inbox; a request at the standard number is served ahead of the origin's live standard work");
  }

  [Test]
  public async Task CheckpointReceptor_HandledInsideAnInteractiveHandling_TheRepairRequestStaysBackgroundAsync() {
    var coordinator = new _verifyCoordinator();
    var transport = new _captureTransport();
    var sp = _checkpointProvider(coordinator, transport, new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped });
    var receptor = new IntegrityCheckpointReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityCheckpointReceptor>.Instance);
    var originId = TrackedGuid.NewMedo().Value;

    using (PriorityContext.Enter(WorkPriority.INTERACTIVE)) {
      await receptor.HandleAsync(_checkpoint(originId, from: 10, to: 20, count: 4));
      await receptor.HandleAsync(_checkpoint(originId, from: 20, to: 20, count: 0, emptyBuckets: true));
    }

    await Assert.That(transport.Published.Single().Envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("the receptor publishes straight to the transport, outside the dispatcher's inheritance; a misclassified checkpoint must not make its repair urgent");
  }

  // ── IntegrityManifestRequestReceptor ────────────────────────────────────

  [Test]
  public async Task ManifestRequestReceptor_EveryManifestChunkIsBackgroundAsync() {
    var coordinator = new _auditCoordinator {
      OwnDigests = [_digest(TrackedGuid.NewMedo().Value, 11, 21, 2), _digest(TrackedGuid.NewMedo().Value, 12, 22, 1), _digest(TrackedGuid.NewMedo().Value, 13, 23, 3)],
    };
    var transport = new _captureTransport();
    var sp = _manifestProvider(coordinator, transport, new StreamIntegrityOptions { MaxDigestsPerManifest = 2, PublishReportEvents = true });
    var receptor = new IntegrityManifestRequestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestIntegrityManifest { RequesterService = "auditor-svc", Topic = "inbox", EventTypes = ["Contracts.TypeX"] });

    await Assert.That(transport.Published.Count).IsEqualTo(2);
    foreach (var (envelope, _, _) in transport.Published) {
      await Assert.That(envelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
        .Because("the auditor compares manifests when it gets to them; a chunk at the standard number competes with the auditor's live work");
    }
  }

  // ── IntegrityManifestReceptor ───────────────────────────────────────────

  [Test]
  public async Task ManifestReceptor_CursorAnswer_TheFollowUpRequestIsBackgroundAsync() {
    var cursor = TrackedGuid.NewMedo().Value;
    var stream = TrackedGuid.NewMedo().Value;
    var coordinator = new _auditCoordinator { ReceivedDigests = [_digest(stream, 41, 42, 5)] };
    var transport = new _captureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _manifestProvider(coordinator, transport, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", DateTimeOffset.UtcNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    await receptor.HandleAsync(_manifest(coordinator, [_digest(stream, 41, 42, 5)]) with {
      SinceSequence = 100,
      ComputedThrough = 300,
      ChunkCount = 1,
      ResumeAfterStreamId = cursor,
    });

    var (followEnvelope, _, _) = transport.Published.Single(p => p.EnvelopeType?.Contains(nameof(RequestIntegrityManifest)) == true);
    await Assert.That(followEnvelope.Priority).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("following a cursor is the same reconciliation continued; every page of it is background");
  }

  // ── helpers ─────────────────────────────────────────────────────────────

  private static IntegrityCheckpoint _checkpoint(Guid originId, long from, long to, int count, bool emptyBuckets = false) => new() {
    CheckpointStreamId = originId,
    OriginServiceId = originId,
    OriginServiceName = "origin-svc",
    RequestTopic = "origin.requests",
    FromCommitSequence = from,
    ToCommitSequence = to,
    Buckets = emptyBuckets ? [] : [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = count }],
  };

  private static StreamDigest _digest(Guid stream, long lo, long hi, int count) => new() {
    TenantScope = "tenant-a",
    EventType = "Contracts.TypeX",
    StreamId = stream,
    DigestLo = lo,
    DigestHi = hi,
    EventCount = count,
  };


  private static IntegrityManifest _manifest(_auditCoordinator coordinator, List<StreamDigest> digests, ManifestLevel level = ManifestLevel.Streams) => new() {
    ManifestStreamId = coordinator.OriginId,
    OriginServiceId = coordinator.OriginId,
    OriginServiceName = "origin-svc",
    Digests = digests,
    Level = level,
    Recomputed = false,
  };

  private static ServiceProvider _checkpointProvider(_verifyCoordinator coordinator, _captureTransport transport, StreamIntegrityOptions options) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IDispatcher>(new _captureDispatcher());
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton(new Whizbang.Core.Observability.StreamIntegrityMetrics(new Whizbang.Core.Observability.WhizbangMetrics()));
    services.AddSingleton(new IntegrityGapTracker());
    services.AddSingleton<Whizbang.Core.Messaging.IntegrityRepairLedger>();
    services.AddSingleton(new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings()));
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IEventTypeProvider>(new _typeProvider());
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider("consumer-svc"));
    services.AddSingleton(Options.Create(options));
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    return services.BuildServiceProvider();
  }

  private static ServiceProvider _manifestProvider(
      _auditCoordinator coordinator, _captureTransport transport, StreamIntegrityOptions? options = null, IntegrityGapTracker? tracker = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IDispatcher>(new _captureDispatcher());
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider("auditor-svc"));
    services.AddSingleton(Options.Create(options ?? new StreamIntegrityOptions { PublishReportEvents = true }));
    if (tracker is not null) {
      services.AddSingleton(tracker);
    }
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    return services.BuildServiceProvider();
  }

  // ── fakes ───────────────────────────────────────────────────────────────

  private sealed class _typeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(IntegrityCheckpointReceptorTests.VerifiedEvent)];
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

  /// <summary>The members every coordinator fake here needs; the rest of the interface keeps its defaults.</summary>
  private abstract class _coordinatorBase : IWorkCoordinator {
    public Guid LocalServiceId { get; } = TrackedGuid.NewMedo().Value;
    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(LocalServiceId);
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task StoreOutboxMessagesAsync(OutboxMessage[] messages, int partitionCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken ct = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken ct = default) => Task.FromResult(true);
  }

  /// <summary>Consumer side of a checkpoint: received nothing, so every bucket is a deficit.</summary>
  private sealed class _verifyCoordinator : _coordinatorBase, IWorkCoordinator {
    public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<ServiceBacklog?>(null);
    public Task<IReadOnlyList<CheckpointBucket>> CountReceivedFromOriginAsync(
      Guid originServiceId, long fromCommitSequence, long toCommitSequence, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<CheckpointBucket>>([]);
  }

  /// <summary>Both sides of a manifest exchange: own digests when asked as an origin, received digests when comparing as a consumer.</summary>
  private sealed class _auditCoordinator : _coordinatorBase, IWorkCoordinator {
    public Guid OriginId { get; } = TrackedGuid.NewMedo().Value;
    public IReadOnlyList<StreamDigest> OwnDigests { get; init; } = [];
    public IReadOnlyList<StreamDigest> ReceivedDigests { get; init; } = [];
    public IReadOnlyList<StreamDigest> ReceivedTypeDigests { get; init; } = [];
    public WindowedDigestResult? WindowedTypeResult { get; init; }
    public Task<IReadOnlyList<StreamDigest>> ComputeStreamDigestsAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes, TimeSpan settleWindow, CancellationToken cancellationToken = default) =>
      Task.FromResult(originServiceId is null ? OwnDigests : ReceivedDigests);
    public Task<IReadOnlyList<StreamDigest>> ComputeTypeDigestsAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes, TimeSpan settleWindow, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<StreamDigest>>(IntegrityDigestMath.RollUpToTypes(originServiceId is null ? OwnDigests : ReceivedDigests));
    public Task<IReadOnlyList<StreamDigest>> GetStreamDigestsAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<StreamDigest>>([]);
    public Task<IReadOnlyList<StreamDigest>> GetTypeDigestsAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<StreamDigest>>(originServiceId is null ? [] : ReceivedTypeDigests);
    public Task<WindowedDigestResult?> ComputeTypeDigestsWindowedAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes,
      long sinceSequence, long? untilSequence, TimeSpan settleWindow, CancellationToken cancellationToken = default) =>
      Task.FromResult(WindowedTypeResult);
    public Task<WindowedDigestResult?> ComputeStreamDigestsWindowedAsync(
      Guid? originServiceId, IReadOnlyList<string>? eventTypes,
      long sinceSequence, long? untilSequence, Guid? resumeAfterStreamId, int maxDigests,
      TimeSpan settleWindow, CancellationToken cancellationToken = default) =>
      Task.FromResult<WindowedDigestResult?>(null);
    public Task<IReadOnlyList<StreamDigest>?> ComputeStreamDigestsForChunkAsync(
      Guid originServiceId, IReadOnlyList<Guid> streamIds,
      long? sinceSequence, long? untilSequence, TimeSpan settleWindow, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<StreamDigest>?>([.. ReceivedDigests.Where(d => streamIds.Contains(d.StreamId))]);
    public Task AdvanceIntegritySealAsync(Guid originServiceId, long through, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<long> GetIntegrityOriginGenerationAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);
    public Task<bool> EnsureIntegritySealGenerationAsync(Guid originServiceId, long generation, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task IntegrityStampRepairWindowsAsync(
      Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, long windowFrom, long windowUntil, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
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

  /// <summary>Accepts publishes (report events are not the subject here); every other member is unused.</summary>
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
