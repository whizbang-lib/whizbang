// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage-round-23 targeted tests for <see cref="IntegrityCheckpointReceptor"/>: the branches
/// <see cref="IntegrityCheckpointReceptorTests"/> never exercises — the feature-disabled early
/// return, the nothing-consumed early return (no consumer registry: every bucket is unconsumed, #2058),
/// and the sender's-own-infrastructure guard inside
/// <c>_sendRepairRequestAsync</c>. No live database: every dependency is an interface satisfied by
/// an in-memory fake, exactly like the sibling suite.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityCheckpointReceptor.cs</code-under-test>
[Category("Shard1")]
public class IntegrityCheckpointReceptorCoverageTests {

  private sealed record CoverageEvent : IEvent {
    [StreamId]
    public Guid Sid { get; init; }
  }

  // The wire form ("Type, Assembly") — checkpoint buckets are built from wh_event_store.event_type,
  // so the subscribed-type filter must match THAT form.
  private static readonly string _verifiedType = TypeNameFormatter.Format(typeof(CoverageEvent));

  /// <summary>
  /// If this guard regressed (stopped short-circuiting, or fired when it shouldn't), a host that
  /// explicitly turned gap detection OFF — to shed load, or because its backend cannot answer the
  /// backlog count — would keep paying the full per-checkpoint scan cost it asked to avoid, or
  /// (the opposite failure) a healthy host would silently stop detecting real gaps.
  /// </summary>
  [Test]
  public async Task GapDetectionDisabled_NeverCountsTheServiceBacklogAsync() {
    var coordinator = new FakeCoordinator();
    var backlogCalls = 0;
    coordinator.OnCountServiceBacklog = () => backlogCalls++;
    var dispatcher = new FakeDispatcher();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton(new IntegrityGapTracker());
    services.AddSingleton(new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings()));
    services.AddSingleton<IntegrityRepairLedger>();
    services.AddSingleton(Options.Create(new StreamIntegrityOptions {
      GapDetectionEnabled = false,
      PublishReportEvents = true,
    }));
    var sp = services.BuildServiceProvider();
    var receptor = new IntegrityCheckpointReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityCheckpointReceptor>.Instance);

    // A real deficit (expected 3, nothing received) that would register as pending if the feature
    // were enabled — proving the return happens BEFORE any of that work, not that there was
    // coincidentally nothing to do.
    await receptor.HandleAsync(_checkpoint(Guid.NewGuid(), "origin-svc", from: 0, to: 5, count: 3));

    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("a disabled gap-detection feature must never publish a report.");
    await Assert.That(backlogCalls).IsEqualTo(0)
      .Because("CountServiceBacklogAsync only runs after the feature-flag guard; any call here " +
               "would mean the guard was skipped and the operator's opt-out was ignored.");
  }

  /// <summary>
  /// With no consumer registry registered nothing is consumed (#2058), so every bucket is someone else's contract. If
  /// the filter instead treated a missing registry as "everything", a host that boots before its registry is wired
  /// would register (and eventually confirm and report) pendings for types it can never legitimately verify.
  /// </summary>
  [Test]
  public async Task NoConsumerRegistryRegistered_TreatsEveryBucketAsUnconsumedAsync() {
    var coordinator = new FakeCoordinator();
    var dispatcher = new FakeDispatcher();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton(new IntegrityGapTracker());
    services.AddSingleton(new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings()));
    services.AddSingleton<IntegrityRepairLedger>();
    // Deliberately NOT registered: IConsumerRegistry.
    services.AddSingleton(Options.Create(new StreamIntegrityOptions { PublishReportEvents = true }));
    var sp = services.BuildServiceProvider();
    var receptor = new IntegrityCheckpointReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityCheckpointReceptor>.Instance);

    var originId = Guid.NewGuid();
    // Would be a real, confirming deficit (expected 5, received 0) if any type were subscribed.
    await receptor.HandleAsync(_checkpoint(originId, "origin-svc", from: 0, to: 5, count: 5));
    await receptor.HandleAsync(_checkpoint(originId, "origin-svc", from: 5, to: 5, count: 0, emptyBuckets: true));

    await Assert.That(dispatcher.Published).IsEmpty()
      .Because("with no consumer registry registered, every bucket is someone else's contract — " +
               "exactly like an explicitly unsubscribed type — and must never confirm a gap.");
  }

  /// <summary>
  /// If this guard regressed, a host that forgot to register <see cref="TransportConsumerOptions"/>
  /// (and never set <c>RepairTopic</c>) would either NullReferenceException deep inside envelope
  /// construction the moment a gap confirmed, or — worse — publish a redelivery request carrying no
  /// return address, which the origin could never route a reply to. Either way the confirmed-gap
  /// report's <c>AutoRepairRequested</c> flag is decided BEFORE this guard runs, so a regression
  /// here is invisible on the report itself — only the log line and the (absent) wire send show it.
  /// </summary>
  [Test]
  public async Task AutoRepairConfirmedGap_MissingOwnReplyTopic_SkipsSendWithoutThrowingAsync() {
    var coordinator = new FakeCoordinator { Counts = _ => [] };   // never heals -> confirms every time
    var dispatcher = new FakeDispatcher();
    var transport = new FakeTransport();
    var logger = new CaptureLogger();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton(new IntegrityGapTracker());
    services.AddSingleton(new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings()));
    services.AddSingleton<IntegrityRepairLedger>();
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IConsumerRegistry>(new FakeEventTypeProvider());
    services.AddSingleton<IServiceInstanceProvider>(new FakeInstanceProvider("consumer-svc"));
    // Deliberately NOT registered: TransportConsumerOptions, and StreamIntegrityOptions.RepairTopic
    // is left null below. Together those are THIS service's own reply address; with neither
    // configured, _sendRepairRequestAsync has nowhere to tell the origin to send the redelivery.
    services.AddSingleton(Options.Create(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      PublishReportEvents = true,
    }));
    var sp = services.BuildServiceProvider();
    var receptor = new IntegrityCheckpointReceptor(sp.GetRequiredService<IServiceScopeFactory>(), logger);

    var originId = Guid.NewGuid();
    await receptor.HandleAsync(_checkpoint(
      originId, "origin-svc", from: 10, to: 20, count: 4, requestTopic: "origin.requests"));
    await receptor.HandleAsync(_checkpoint(
      originId, "origin-svc", from: 20, to: 20, count: 0, emptyBuckets: true, requestTopic: "origin.requests"));

    await Assert.That(transport.Published).IsEmpty()
      .Because("the guard must stop the send before anything reaches the wire.");

    List<(Microsoft.Extensions.Logging.LogLevel Level, int EventId, string Message)> entries;
    lock (logger.Entries) { entries = [.. logger.Entries]; }
    await Assert.That(entries.Any(e =>
        e.EventId == 51 && e.Message.Contains("topic=True") && e.Message.Contains("transport=False")))
      .IsTrue()
      .Because("the skip must be logged with the SPECIFIC missing piece named — here the reply " +
               "topic, not the transport (which WAS registered) — so an operator can fix the right thing.");

    var report = (IntegrityGapDetected)dispatcher.Published.Single();
    await Assert.That(report.AutoRepairRequested).IsTrue()
      .Because("autoRepair is decided by the policy/ledger before the send is attempted; the " +
               "report's flag reflects that decision, not whether anything actually reached the " +
               "wire — exactly the operator trap this guard's log line exists to explain.");
  }

  /// <summary>
  /// A host with housekeeping arbitration must see the checkpoint hold the integrity slot for the
  /// whole handler and give it back afterwards. If the hold were skipped, the cleanup sweep would walk
  /// the same tables concurrently; if it leaked, the sweep would be disabled for the life of the
  /// process.
  /// </summary>
  [Test]
  public async Task HousekeepingRegistered_HoldsTheIntegritySlotDuringTheCheckpointAndReleasesItAfterAsync() {
    var housekeeping = new HousekeepingCoordinator();
    HousekeepingCoordinator.Decision? cleanupDuringCheckpoint = null;
    var coordinator = new FakeCoordinator {
      // Mid-handler, after the hold is taken: ask for the cleanup slot the way the sweep would.
      OnCountServiceBacklog = () =>
        cleanupDuringCheckpoint = housekeeping.TryBegin(HousekeepingCoordinator.Activity.Maintenance, backlog: null),
    };

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IDispatcher>(new FakeDispatcher());
    services.AddSingleton(new IntegrityGapTracker());
    services.AddSingleton(new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings()));
    services.AddSingleton<IntegrityRepairLedger>();
    services.AddSingleton(housekeeping);
    services.AddSingleton(Options.Create(new StreamIntegrityOptions()));
    var sp = services.BuildServiceProvider();
    var receptor = new IntegrityCheckpointReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityCheckpointReceptor>.Instance);

    await receptor.HandleAsync(_checkpoint(Guid.NewGuid(), "origin-svc", from: 0, to: 5, count: 0, emptyBuckets: true));

    await Assert.That(cleanupDuringCheckpoint.HasValue).IsTrue()
      .Because("the handler must have reached the backlog count, where the probe runs");
    await Assert.That(cleanupDuringCheckpoint!.Value.Granted).IsFalse();
    await Assert.That(cleanupDuringCheckpoint.Value.Reason).IsEqualTo(HousekeepingCoordinator.Verdict.HigherPriorityRunning)
      .Because("while the checkpoint runs it holds the integrity slot, which outranks cleanup");

    var afterwards = housekeeping.TryBegin(HousekeepingCoordinator.Activity.Integrity, backlog: null);
    await Assert.That(afterwards.Granted).IsTrue()
      .Because("the hold is released when the handler returns; a leaked slot would refuse this as AlreadyRunning");
    housekeeping.End(HousekeepingCoordinator.Activity.Integrity);
  }

  // ── #2058: only what this service consumes is verified ─────────────────

  private const string UNCONSUMED_TYPE = "Contracts.KnownButUnconsumed, Contracts";
  private const string RECEPTOR_ONLY_TYPE = "Contracts.ReceptorOnly, Contracts";

  /// <summary>
  /// A consumer with no perspectives, whose receptor handles A, receives a checkpoint naming A and B with a deficit in
  /// both. A is verified (and confirms on the next checkpoint); B, which nothing here consumes, is never verified:
  /// its redelivery would be discarded at the receive boundary, so the deficit could never heal.
  /// </summary>
  [Test]
  public async Task NoPerspectives_VerifiesTheConsumedType_AndNeverTheTypeNothingHereConsumesAsync() {
    var dispatcher = new FakeDispatcher();
    var receptor = _consumerReceptor(dispatcher, perspectives: null, consumed: [_verifiedType]);
    var originId = Guid.NewGuid();

    await receptor.HandleAsync(_checkpointOf(originId, from: 0, to: 5, (_verifiedType, 3), (UNCONSUMED_TYPE, 4)));
    await receptor.HandleAsync(_checkpointOf(originId, from: 5, to: 5));

    var reported = dispatcher.Published.OfType<IntegrityGapDetected>().Select(r => r.EventType).ToList();
    await Assert.That(reported).IsEquivalentTo([_verifiedType])
      .Because("the consumed type is verified even without a perspective, and the unconsumed one never is");
  }

  /// <summary>A consumer with a perspective for A and a receptor for R verifies R too.</summary>
  [Test]
  public async Task PerspectiveAndReceptor_VerifiesTheReceptorOnlyTypeAsync() {
    var dispatcher = new FakeDispatcher();
    var receptor = _consumerReceptor(dispatcher, perspectives: new PerspectiveEvents(),
      consumed: [_verifiedType, RECEPTOR_ONLY_TYPE]);
    var originId = Guid.NewGuid();

    await receptor.HandleAsync(_checkpointOf(originId, from: 0, to: 5, (RECEPTOR_ONLY_TYPE, 2)));
    await receptor.HandleAsync(_checkpointOf(originId, from: 5, to: 5));

    var reported = dispatcher.Published.OfType<IntegrityGapDetected>().Select(r => r.EventType).ToList();
    await Assert.That(reported).IsEquivalentTo([RECEPTOR_ONLY_TYPE])
      .Because("an event only a receptor consumes is stored here, so its count is this service's contract");
  }

  /// <summary>
  /// A consumer that absorbs a namespace stores its events though nothing consumes them, so it verifies them too: the
  /// receive boundary's decision (the discard policy) is the filter, not the receptor list alone.
  /// </summary>
  [Test]
  public async Task AbsorbedNamespace_WithNothingConsumingIt_IsVerifiedAsync() {
    const string absorbedType = "Contracts.Jobs.JobStarted, Contracts";
    var dispatcher = new FakeDispatcher();
    var receptor = _consumerReceptor(dispatcher, perspectives: null, consumed: [], absorbedNamespaces: ["contracts.jobs"]);
    var originId = Guid.NewGuid();

    await receptor.HandleAsync(_checkpointOf(originId, from: 0, to: 5, (absorbedType, 3), (UNCONSUMED_TYPE, 4)));
    await receptor.HandleAsync(_checkpointOf(originId, from: 5, to: 5));

    var reported = dispatcher.Published.OfType<IntegrityGapDetected>().Select(r => r.EventType).ToList();
    await Assert.That(reported).IsEquivalentTo([absorbedType])
      .Because("an absorbed namespace's events are stored here, so a deficit in them is a real gap");
  }

  private static IntegrityCheckpointReceptor _consumerReceptor(
      FakeDispatcher dispatcher, IEventTypeProvider? perspectives, string[] consumed, string[]? absorbedNamespaces = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(new FakeCoordinator());
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton(new IntegrityGapTracker());
    services.AddSingleton(new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings()));
    services.AddSingleton<IntegrityRepairLedger>();
    if (perspectives is not null) {
      services.AddSingleton(perspectives);
    }
    var registry = new ConsumedRegistry([.. consumed.Select(t => new ConsumedMessageInfo(t, IsEvent: true))]);
    services.AddSingleton<IConsumerRegistry>(registry);
    if (absorbedNamespaces is not null) {
      services.AddSingleton<Whizbang.Core.Routing.IMessageDiscardPolicy>(new Whizbang.Core.Routing.MessageDiscardPolicy(
        registry, NullLogger<Whizbang.Core.Routing.MessageDiscardPolicy>.Instance,
        new System.Diagnostics.Metrics.Meter("integrity-checkpoint-absorbed-test"),
        Options.Create(new Whizbang.Core.Routing.RoutingOptions().AbsorbNamespaces(absorbedNamespaces)),
        new EventMarkerResolver(NullMessageTypeCatalog.Instance)));
    }
    services.AddSingleton(Options.Create(new StreamIntegrityOptions { PublishReportEvents = true }));
    var sp = services.BuildServiceProvider();
    return new IntegrityCheckpointReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityCheckpointReceptor>.Instance);
  }

  private static IntegrityCheckpoint _checkpointOf(
      Guid originId, long from, long to, params (string EventType, int Count)[] buckets) => new() {
        CheckpointStreamId = originId,
        OriginServiceId = originId,
        OriginServiceName = "origin-svc",
        FromCommitSequence = from,
        ToCommitSequence = to,
        Buckets = [.. buckets.Select(b => new CheckpointBucket { TenantScope = "tenant-a", EventType = b.EventType, Count = b.Count })],
      };

  /// <summary>The perspective event types: the perspective folds the verified event only.</summary>
  private sealed class PerspectiveEvents : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(CoverageEvent)];
  }

  private sealed class ConsumedRegistry(ConsumedMessageInfo[] consumed) : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => false;
    public bool HasAnyConsumer(string messageType) => consumed.Any(c => c.TypeName == messageType);
    public IReadOnlyList<ConsumedMessageInfo> GetConsumedMessages() => consumed;
  }

  // ── fixture helpers ────────────────────────────────────────────────────

  private static IntegrityCheckpoint _checkpoint(
      Guid originId, string originName, long from, long to, int count,
      bool emptyBuckets = false, string? requestTopic = null) => new() {
        CheckpointStreamId = originId,
        OriginServiceId = originId,
        OriginServiceName = originName,
        RequestTopic = requestTopic,
        FromCommitSequence = from,
        ToCommitSequence = to,
        Buckets = emptyBuckets
          ? []
          : [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = count }],
      };

  // ── fakes ───────────────────────────────────────────────────────────────

  /// <summary>The consumer registry: this service consumes these events.</summary>
  private sealed class FakeEventTypeProvider : IConsumerRegistry {
    private static readonly Type[] _events = [typeof(CoverageEvent)];
    public bool HasAnyConsumer(string messageType) => _events.Any(e => TypeNameFormatter.Format(e) == messageType);
    public IReadOnlyList<ConsumedMessageInfo> GetConsumedMessages() =>
      [.. _events.Select(e => new ConsumedMessageInfo(TypeNameFormatter.Format(e), IsEvent: true))];
  }

  private sealed class FakeInstanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
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

  /// <summary>
  /// Minimal <see cref="IWorkCoordinator"/> fake. Most interface members carry a default
  /// implementation; only the handful with none are overridden here, plus the two members this
  /// receptor actually reads (<see cref="GetLocalServiceIdAsync"/>,
  /// <see cref="CountReceivedFromOriginAsync"/>) and the one call-counted for proof of
  /// short-circuiting (<see cref="CountServiceBacklogAsync"/>).
  /// </summary>
  private sealed class FakeCoordinator : IWorkCoordinator {
    public Guid LocalServiceId { get; init; } = Guid.NewGuid();
    public Func<(Guid Origin, long From, long To), IReadOnlyList<CheckpointBucket>> Counts { get; set; } = _ => [];
    public Action? OnCountServiceBacklog { get; set; }

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(LocalServiceId);

    public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default) {
      OnCountServiceBacklog?.Invoke();
      return ValueTask.FromResult<ServiceBacklog?>(null);
    }

    public Task<IReadOnlyList<CheckpointBucket>> CountReceivedFromOriginAsync(
      Guid originServiceId, long fromCommitSequence, long toCommitSequence, CancellationToken cancellationToken = default) =>
      Task.FromResult(Counts((originServiceId, fromCommitSequence, toCommitSequence)));

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class FakeTransport : ITransport {
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

  /// <summary>Captures PublishAsync payloads; every other dispatcher member is unused here.</summary>
  private sealed class FakeDispatcher : IDispatcher {
    public List<object> Published { get; } = [];

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
      Published.Add(eventData!);
      return Task.FromResult<IDeliveryReceipt>(new Receipt());
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

    private sealed class Receipt : IDeliveryReceipt {
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

  private sealed class CaptureLogger : Microsoft.Extensions.Logging.ILogger<IntegrityCheckpointReceptor> {
    public List<(Microsoft.Extensions.Logging.LogLevel Level, int EventId, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (Entries) { Entries.Add((logLevel, eventId.Id, formatter(state, exception))); }
    }
  }
}
