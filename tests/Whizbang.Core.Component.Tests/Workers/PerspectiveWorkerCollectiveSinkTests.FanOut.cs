// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2620: a foreign event a perspective fans out reaches the sink on its <c>__collective__</c> row (#2619), and the sink
/// dispatches it through <see cref="ICollectiveDispatcher"/> as a <see cref="FanOutCollectiveEvent"/> scoped to the
/// event's stream and tenant, as a collective's apply runs. Both queue reads run every case
/// (<see cref="PerspectiveWorkerCollectiveSinkQueueHeadTests"/>).
/// </summary>
public partial class PerspectiveWorkerCollectiveSinkTests {
  private const string FAN_OUT_TENANT = "tenant-fan-out";
  private const int SINK_EVENTS_NOT_APPLIED_HERE_EVENT_ID = 95;

  [Test]
  [Timeout(30000)]
  public async Task FanOut_TheSinkDispatchesTheForeignEvent_WrappedWithItsStreamAndTenantAsync(CancellationToken cancellationToken) {
    var customer = TrackedGuid.New().Value;
    var renamedId = TrackedGuid.New().Value;
    var renamed = new CustomerRenamed("Ada");
    var dispatcher = new RecordingDispatcher();
    var work = _sinkWork(customer);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [work],
      eventStore: _storeOf(customer, _scoped(renamedId, renamed, FAN_OUT_TENANT)),
      registry: new Registry([typeof(CustomerRenamed)]),
      dispatcher: dispatcher,
      fanOuts: [_renamedFanOut]);
    coordinator.SinkQueue = [new CollectiveSinkQueueEntry(work.WorkId, renamedId, CommitSequence: 1)];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(cancellationToken);
    await harness.CompletionCapture.EventWorkIdsCaptured(work.WorkId).WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    var (evt, eventId) = dispatcher.Calls.Single();
    await Assert.That(eventId).IsEqualTo(renamedId).Because("the apply's progress record is kept under the event's id");
    var fanOut = (FanOutCollectiveEvent)evt;
    await Assert.That(fanOut.Event).IsSameReferenceAs(renamed);
    var scope = (FanOutScope)fanOut.Scope;
    await Assert.That(scope.StreamId).IsEqualTo(customer).Because("the rows whose key is the customer's stream id");
    await Assert.That(scope.TenantId).IsEqualTo(FAN_OUT_TENANT);
  }

  [Test]
  [Timeout(30000)]
  public async Task FanOut_AnEventWithNoTenant_TargetsNothing_AndItsRowIsCompletedAsync(CancellationToken cancellationToken) {
    var customer = TrackedGuid.New().Value;
    var renamedId = TrackedGuid.New().Value;
    var dispatcher = new RecordingDispatcher();
    var work = _sinkWork(customer);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [work],
      eventStore: _storeOf(customer, _envelope(renamedId, new CustomerRenamed("Ada"))),
      registry: new Registry([typeof(CustomerRenamed)]),
      dispatcher: dispatcher,
      fanOuts: [_renamedFanOut]);
    coordinator.SinkQueue = [new CollectiveSinkQueueEntry(work.WorkId, renamedId, CommitSequence: 1)];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await harness.CompletionCapture.EventWorkIdsCaptured(work.WorkId).WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(dispatcher.Calls).IsEmpty()
      .Because("a fan-out targets the rows of the event's own tenant, and a system-scoped event has none");
  }

  [Test]
  [Timeout(30000)]
  public async Task FanOut_ASinkRowForAnEventThisBuildDoesNotApply_IsCompleted_CountedAndNamed_NeverSilentlyAsync(CancellationToken cancellationToken) {
    // #2619 mixed fleet: a row for an event that is neither a collective nor a fan-out here (a newer release's fan-out,
    // met by an instance that overrode its stand-down) cannot be applied here. It is completed as before, but counted on
    // whizbang.collectives.unhandled and logged with its type, so the loss is never silent.
    var customer = TrackedGuid.New().Value;
    var renamedId = TrackedGuid.New().Value;
    var dispatcher = new RecordingDispatcher();
    var work = _sinkWork(customer);
    using var factory = new Whizbang.Core.Tests.Observability.TestMeterFactory();
    var metrics = new CompositeMetrics(new WhizbangMetrics(factory));
    var logger = new DeadLetterRecordLogger<PerspectiveWorker>();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [work],
      eventStore: _storeOf(customer, _scoped(renamedId, new CustomerRenamed("Ada"), FAN_OUT_TENANT)),
      registry: new Registry([typeof(CustomerRenamed)]),
      dispatcher: dispatcher,
      compositeMetrics: metrics,
      logger: logger);
    coordinator.SinkQueue = [new CollectiveSinkQueueEntry(work.WorkId, renamedId, CommitSequence: 1)];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await harness.CompletionCapture.EventWorkIdsCaptured(work.WorkId).WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    var meter = factory.CreatedMeters.Single(m => m.Name == CompositeMetrics.METER_NAME);
    await Assert.That(dispatcher.Calls).IsEmpty();
    await Assert.That(Whizbang.Core.Tests.Observability.ProbeMeterReader.ReadTotal(meter, "whizbang.collectives.unhandled")).IsEqualTo(1);
    var named = logger.Entries.Single(e => e.EventId == SINK_EVENTS_NOT_APPLIED_HERE_EVENT_ID);
    await Assert.That(named.Level).IsEqualTo(Microsoft.Extensions.Logging.LogLevel.Warning);
    await Assert.That(named.Message).Contains(TypeNameFormatter.Format(typeof(CustomerRenamed)));
  }

  [Test]
  [Timeout(30000)]
  public async Task FanOut_AFailingApply_IsReportedAsASinkFailure_AndTheHostKeepsRunningAsync(CancellationToken cancellationToken) {
    var customer = TrackedGuid.New().Value;
    var renamedId = TrackedGuid.New().Value;
    var dispatcher = new ThrowingDispatcher();
    var work = _sinkWork(customer);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [work],
      eventStore: _storeOf(customer, _scoped(renamedId, new CustomerRenamed("Ada"), FAN_OUT_TENANT)),
      registry: new Registry([typeof(CustomerRenamed)]),
      dispatcher: dispatcher,
      fanOuts: [_renamedFanOut]);
    coordinator.SinkQueue = [new CollectiveSinkQueueEntry(work.WorkId, renamedId, CommitSequence: 1)];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await coordinator.FirstFailure.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(worker.ExecuteTask.IsFaulted).IsFalse().Because("one poison fan-out must not stop the host");
    var failure = coordinator.ReportedFailures[0];
    await Assert.That(failure.PerspectiveName).IsEqualTo(CollectiveRouting.SINK_PERSPECTIVE_NAME);
    await Assert.That(failure.StreamId).IsEqualTo(customer);
  }

  [Test]
  [Timeout(30000)]
  public async Task FanOut_TheDrain_RoutesAFanOutEventToTheSink_BesideThePerspectivesThatReadItAsync(CancellationToken cancellationToken) {
    // The drain path: a received fan-out event is claimed as a stream and offered to the sink as well as to its own
    // stream's perspectives; the sink reloads and dispatches it.
    var customer = TrackedGuid.New().Value;
    var renamedId = TrackedGuid.New().Value;
    var dispatcher = new RecordingDispatcher();
    var envelope = _scoped(renamedId, new CustomerRenamed("Ada"), FAN_OUT_TENANT);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      work: [],
      eventStore: new EventStore { Envelopes = { [customer] = [envelope] }, Deserialized = [envelope] },
      registry: new Registry([typeof(CustomerRenamed)]),
      dispatcher: dispatcher,
      drainStreamIds: [customer],
      streamEvents: [_raw(customer, renamedId)],
      fanOuts: [_renamedFanOut]);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(dispatcher.Calls.Single().Event).IsTypeOf<FanOutCollectiveEvent>();
    await Assert.That(envelope.Payload).IsTypeOf<CustomerRenamed>()
      .Because("the drain's own perspectives are handed the foreign event; the sink wraps a copy");
  }

  [Test]
  [Timeout(30000)]
  public async Task FanOut_TheDrain_AFanOutEventNoPerspectiveHereReads_IsAppliedByTheSinkAloneAsync(CancellationToken cancellationToken) {
    // Only the sink applies it here, so the drain tracks no lifecycle for it (the receive path fired it already).
    var customer = TrackedGuid.New().Value;
    var renamedId = TrackedGuid.New().Value;
    var dispatcher = new RecordingDispatcher();
    var envelope = _scoped(renamedId, new CustomerRenamed("Ada"), FAN_OUT_TENANT);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      work: [],
      eventStore: new EventStore { Envelopes = { [customer] = [envelope] }, Deserialized = [envelope] },
      registry: new Registry([typeof(CustomerRenamed)], perspectiveEventTypes: [typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      drainStreamIds: [customer],
      streamEvents: [_raw(customer, renamedId)],
      fanOuts: [_renamedFanOut]);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(dispatcher.Calls.Single().Event).IsTypeOf<FanOutCollectiveEvent>();
  }

  [Test]
  [Timeout(30000)]
  public async Task FanOut_ASinkRowForAnEventThisBuildDoesNotApply_IsNamedInAWarning_WithNoMetersWiredAsync(CancellationToken cancellationToken) {
    var customer = TrackedGuid.New().Value;
    var renamedId = TrackedGuid.New().Value;
    var work = _sinkWork(customer);
    var logger = new DeadLetterRecordLogger<PerspectiveWorker>();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [work],
      eventStore: _storeOf(customer, _scoped(renamedId, new CustomerRenamed("Ada"), FAN_OUT_TENANT)),
      registry: new Registry([typeof(CustomerRenamed)]),
      dispatcher: new RecordingDispatcher(),
      logger: logger);
    coordinator.SinkQueue = [new CollectiveSinkQueueEntry(work.WorkId, renamedId, CommitSequence: 1)];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await harness.CompletionCapture.EventWorkIdsCaptured(work.WorkId).WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(logger.Entries.Count(e => e.EventId == SINK_EVENTS_NOT_APPLIED_HERE_EVENT_ID)).IsEqualTo(1);
  }

  // #2622: after each fan-out it applies, the sink asks every fan-out of the event's type to rewind the rows it reached
  // late, with the event's stream and tenant; a fan-out of another type is not asked.
  [Test]
  [Timeout(30000)]
  public async Task CollectiveSink_AFanOutItApplies_AsksEachFanOutOfItsTypeToRewindTheRowsItReachedLateAsync(CancellationToken cancellationToken) {
    var customer = TrackedGuid.New().Value;
    var renamedId = TrackedGuid.New().Value;
    var rewinds = new RecordingRewinds();
    var work = _sinkWork(customer);

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [work],
      eventStore: _storeOf(customer, _scoped(renamedId, new CustomerRenamed("Ada"), FAN_OUT_TENANT)),
      registry: new Registry([typeof(CustomerRenamed)]),
      dispatcher: new RecordingDispatcher(),
      fanOuts: [_renamedFanOut, _renamedToInvoicesFanOut, _deletedFanOut],
      lateApplyRewinds: rewinds);
    coordinator.SinkQueue = [new CollectiveSinkQueueEntry(work.WorkId, renamedId, CommitSequence: 1)];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await harness.CompletionCapture.EventWorkIdsCaptured(work.WorkId).WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(rewinds.Calls).IsEquivalentTo([
      (_renamedFanOut, customer, FAN_OUT_TENANT, renamedId),
      (_renamedToInvoicesFanOut, customer, FAN_OUT_TENANT, renamedId),
    ]).Because("each perspective that fans the rename out has its rows checked once; the delete's fan-out is another type");
  }

  [Test]
  [Timeout(30000)]
  public async Task CollectiveSink_ACollectiveItApplies_AsksNoFanOutToRewindAsync(CancellationToken cancellationToken) {
    var streamId = TrackedGuid.New().Value;
    var eventId = TrackedGuid.New().Value;
    var dispatcher = new RecordingDispatcher();
    var rewinds = new RecordingRewinds();

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [_sinkWork(streamId)],
      eventStore: new EventStore { Envelopes = { [streamId] = [_envelope(eventId, new TestCollectiveEvent { Scope = new TenantCollectiveScope("t") })] } },
      registry: new Registry([typeof(TestCollectiveEvent)]),
      dispatcher: dispatcher,
      fanOuts: [_renamedFanOut],
      lateApplyRewinds: rewinds);

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await dispatcher.FirstDispatch.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(rewinds.Calls).IsEmpty().Because("a collective's own rows are found by its scope, not by a fan-out key");
  }

  // #2637: a sync wait on a perspective a foreign event fans out to completes once the sink's apply commits, for each
  // perspective that fans the event out, and for no other.
  [Test]
  [Timeout(30000)]
  public async Task CollectiveSink_AFanOutItApplies_AnswersASyncWaitOnEachPerspectiveTheEventFansOutToAsync(CancellationToken cancellationToken) {
    var customer = TrackedGuid.New().Value;
    var renamedId = TrackedGuid.New().Value;
    var work = _sinkWork(customer);
    var tracker = new SyncEventTracker();
    var orders = TypeNameFormatter.GetPerspectiveName(typeof(OrdersPerspective));
    var invoices = TypeNameFormatter.GetPerspectiveName(typeof(InvoicesPerspective));
    const string UNRELATED = "Unrelated.Perspective";
    foreach (var perspective in new[] { orders, invoices, UNRELATED }) {
      tracker.TrackEvent(typeof(CustomerRenamed), renamedId, customer, perspective);
    }
    var ordersWait = tracker.WaitForPerspectiveEventsAsync([renamedId], orders, Timeout.InfiniteTimeSpan, cancellationToken: cancellationToken);
    var invoicesApplied = tracker.WhenAppliedAsync(renamedId, invoices, cancellationToken);
    await Assert.That(ordersWait.IsCompleted).IsFalse().Because("the control: the rename is pending for the orders");

    using var cts = new CancellationTokenSource();
    var (worker, harness, coordinator) = _createWorker(
      [work],
      eventStore: _storeOf(customer, _scoped(renamedId, new CustomerRenamed("Ada"), FAN_OUT_TENANT)),
      registry: new Registry([typeof(CustomerRenamed)]),
      dispatcher: new RecordingDispatcher(),
      syncEventTracker: tracker,
      fanOuts: [_toOrders, _toInvoices, _toInvoicesByBilling]);
    coordinator.SinkQueue = [new CollectiveSinkQueueEntry(work.WorkId, renamedId, CommitSequence: 1)];

    await worker.StartAsync(cts.Token);
    _ = WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    var synced = await ordersWait.WaitAsync(cancellationToken);
    await invoicesApplied.WaitAsync(cancellationToken);
    await harness.CompletionCapture.EventWorkIdsCaptured(work.WorkId).WaitAsync(cancellationToken);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(synced).IsTrue();
    await Assert.That(tracker.GetPendingEvents(customer, invoices)).IsEmpty()
      .Because("the invoices perspective fans the rename out by two keys, and is answered once for both");
    await Assert.That(tracker.GetPendingEvents(customer, UNRELATED).Select(e => e.EventId)).IsEquivalentTo([renamedId])
      .Because("only the perspectives that fan the event out are answered by its apply");
  }

  private sealed class OrdersPerspective;

  private sealed class InvoicesPerspective;

  private static readonly PerspectiveFanOutEntry _toOrders = new(
    typeof(OrdersPerspective), typeof(FanOutOrder), typeof(CustomerRenamed), nameof(FanOutOrder.CustomerId), "customer_id",
    static (_, _) => throw new InvalidOperationException("the sink never calls Apply itself; the dispatcher does"),
    (Expression<Func<FanOutOrder, object?>>)(row => row.CustomerId));

  private static readonly PerspectiveFanOutEntry _toInvoices = new(
    typeof(InvoicesPerspective), typeof(Invoice), typeof(CustomerRenamed), nameof(Invoice.CustomerId), "customer_id",
    static (_, _) => throw new InvalidOperationException("the sink never calls Apply itself; the dispatcher does"),
    (Expression<Func<Invoice, object?>>)(row => row.CustomerId));

  private static readonly PerspectiveFanOutEntry _toInvoicesByBilling = new(
    typeof(InvoicesPerspective), typeof(Invoice), typeof(CustomerRenamed), nameof(Invoice.BillingId), "billing_id",
    static (_, _) => throw new InvalidOperationException("the sink never calls Apply itself; the dispatcher does"),
    (Expression<Func<Invoice, object?>>)(row => row.BillingId));

  private sealed class RecordingRewinds : IFanOutLateApplyRewinds {
    private readonly System.Collections.Concurrent.ConcurrentQueue<(PerspectiveFanOutEntry, Guid, string, Guid)> _calls = new();
    public IReadOnlyList<(PerspectiveFanOutEntry, Guid, string, Guid)> Calls => [.. _calls];

    public Task<int> RewindRowsAppliedLateAsync(
        PerspectiveFanOutEntry fanOut, Guid streamId, string tenantId, Guid eventId, CancellationToken cancellationToken) {
      _calls.Enqueue((fanOut, streamId, tenantId, eventId));
      return Task.FromResult(0);
    }
  }

  // ---------- fixtures ----------

  private sealed record CustomerRenamed(string Name) : IEvent;

  private sealed class FanOutOrder {
    public Guid CustomerId { get; init; }
  }

  private static readonly PerspectiveFanOutEntry _renamedFanOut = new(
    typeof(object), typeof(FanOutOrder), typeof(CustomerRenamed), nameof(FanOutOrder.CustomerId), "customer_id",
    static (_, _) => throw new InvalidOperationException("the sink never calls Apply itself; the dispatcher does"),
    (Expression<Func<FanOutOrder, object?>>)(row => row.CustomerId));

  private sealed class Invoice {
    public Guid CustomerId { get; init; }
    public Guid BillingId { get; init; }
  }

  private sealed record CustomerDeleted : IEvent;

  private static readonly PerspectiveFanOutEntry _renamedToInvoicesFanOut = new(
    typeof(string), typeof(Invoice), typeof(CustomerRenamed), nameof(Invoice.CustomerId), "customer_id",
    static (_, _) => throw new InvalidOperationException("the sink never calls Apply itself; the dispatcher does"),
    (Expression<Func<Invoice, object?>>)(row => row.CustomerId));

  private static readonly PerspectiveFanOutEntry _deletedFanOut = new(
    typeof(object), typeof(FanOutOrder), typeof(CustomerDeleted), nameof(FanOutOrder.CustomerId), "customer_id",
    static (_, _) => throw new InvalidOperationException("the sink never calls Apply itself; the dispatcher does"),
    (Expression<Func<FanOutOrder, object?>>)(row => row.CustomerId));

  private static EventStore _storeOf(Guid stream, MessageEnvelope<IEvent> envelope) =>
    new() { Envelopes = { [stream] = [envelope] }, Deserialized = [envelope] };

  private static MessageEnvelope<IEvent> _scoped(Guid eventId, IEvent payload, string tenant) => new() {
    MessageId = new MessageId(eventId),
    Payload = payload,
    Hops = [new MessageHop {
      Type = HopType.Current,
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = tenant }),
    }],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  private sealed class FanOutRegistry(IReadOnlyList<PerspectiveFanOutEntry> entries) : IPerspectiveFanOutRegistry {
    public IReadOnlyList<PerspectiveFanOutEntry> Entries => entries;
  }
}
