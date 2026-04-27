using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Integration.Tests.Perspectives;

/// <summary>
/// Lock-in integration tests for the Apply-exactly-once contract at the perspective
/// dispatch layer. The contract: <c>Apply(TModel, TEvent)</c> is invoked exactly once
/// per event per perspective per stream. The projection's <c>Apply</c> methods are NOT
/// required to be idempotent — the framework guarantees single dispatch.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Context — why these tests exist.</strong> A bug surfaced in a consumer application where
/// <c>OrderOrderLineRowAddedEvent</c>s produced twice as many rows as events
/// (5 events → 10 rows, duplicate RowIds). Investigation showed the event store was
/// correct and no receptor double-fired; the doubling was purely at the
/// <c>Apply(TModel, TEvent)</c> dispatch layer in the generated
/// <c>IPerspectiveRunner</c>. ~35 % of dev streams were affected with 196 exactly-doubled
/// essential-function jobs.
/// </para>
/// <para>
/// Plan: <c>plans/receptor-chaos-scenarios-deferred.md</c> and
/// <c>~/.claude/plans/ok-we-have-an-quirky-newt.md</c>.
/// </para>
/// <para>
/// Four suspect root causes. Tests below target each.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/apply-exactly-once</docs>
[Category("Integration")]
[NotInParallel("PerspectiveApplyExactlyOnceIntegration")]
public class PerspectiveApplyExactlyOnceTests {

  // ==================== Scenario 1: drain-mode + standard-mode do NOT co-fire ====================

  /// <summary>
  /// Suspect #1 in the plan: a stream appears in BOTH <c>WorkBatch.PerspectiveStreamIds</c>
  /// (drain mode) AND <c>WorkBatch.PerspectiveWork</c> (standard mode). Drain mode processes
  /// the stream, then standard mode's <c>Parallel.ForEachAsync</c> loop processes it again.
  /// </summary>
  /// <remarks>
  /// Expected behavior: the runner is invoked AT MOST ONCE per (stream, perspective) per
  /// cycle. Either drain-mode's <c>RunWithEventsAsync</c> OR standard-mode's <c>RunAsync</c>
  /// fires — never both. The guard at <c>PerspectiveWorker.cs:566</c> clears
  /// <c>groupedWork</c> only when drain mode populated <c>batchProcessedEvents</c>; if that
  /// gate has any gap, both paths fire and every event is applied twice.
  /// </remarks>
  [Test]
  public async Task DrainModeAndStandardMode_DoNotBothFireForSameStream_Async() {
    // Arrange — coordinator returns a WorkBatch where the SAME streamId appears in BOTH
    // PerspectiveStreamIds (drain path) AND PerspectiveWork (standard path). This mirrors the
    // production condition we're investigating: an incoming batch that carries leased events
    // via drain mode plus a legacy per-event queue row for the same perspective.
    var streamId = TrackedGuid.NewMedo().Value;
    var eventId = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "Test.DoubleDispatchPerspective";
    var runner = new _pathTrackingRunner(Status: PerspectiveProcessingStatus.Completed, AdvanceToEventId: eventId);

    var perspectiveWork = new PerspectiveWork {
      WorkId = Guid.CreateVersion7(),
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastProcessedEventId = null,
      PartitionNumber = 1
    };

    var coordinator = new _dualPathCoordinator {
      StreamIdsToReturnOnce = [streamId],
      PerspectiveWorkToReturnOnce = [perspectiveWork],
      StreamEventsToReturn = [
        new StreamEventData {
          StreamId = streamId,
          EventId = eventId,
          EventType = TypeNameFormatter.Format(typeof(_fakeApplyEvent)),
          EventData = JsonSerializer.Serialize(new _fakeApplyEvent(1)),
          Metadata = null,
          Scope = null,
          EventWorkId = Guid.CreateVersion7()
        }
      ]
    };

    var envelope = new MessageEnvelope<IEvent> {
      MessageId = new MessageId(eventId),
      Payload = new _fakeApplyEvent(1),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    var eventStore = new _applyTestEventStore { StreamEnvelopes = { [streamId] = [envelope] } };
    var registry = new _singleRegistry(runner, perspectiveName, [typeof(_fakeApplyEvent)]);

    // Act — drive the worker. Wait for TWO cycles: cycle 2 starting means cycle 1 finished
    // (drain branch + standard branch + completion reporting all drained). No timing-based waits.
    using var cts = new CancellationTokenSource();
    var (worker, harness) = _createWorker(coordinator, registry, eventStore);
    var workerTask = worker.StartAsync(cts.Token);
    _ = Whizbang.Testing.Workers.WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await coordinator.WaitForCyclesAsync(minCycles: 2, timeout: TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { /* expected */ }

    // Assert — at most ONE invocation recorded for (streamId, perspectiveName) across all
    // runner paths. If the guard at PerspectiveWorker.cs:566 fails, both RunWithEventsAsync
    // (drain) AND RunAsync (standard) would fire and this assertion would hit 2.
    var invocationsForPair = runner.Invocations
      .Where(i => i.StreamId == streamId && i.PerspectiveName == perspectiveName)
      .ToList();
    await Assert.That(invocationsForPair.Count).IsLessThanOrEqualTo(1).Because(
      "Apply-exactly-once contract: when a stream appears in both PerspectiveStreamIds (drain) "
      + "and PerspectiveWork (standard), exactly one dispatch path must fire. Recorded invocations: "
      + string.Join(", ", invocationsForPair.Select(i => $"{i.Path}→{i.EventId:N}")));

    // At least one path must have fired — otherwise the test is vacuous.
    await Assert.That(invocationsForPair.Count).IsGreaterThanOrEqualTo(1).Because(
      "Either drain or standard must have run — if neither fired, the test setup is broken.");
  }

  // ==================== Scenario 1b: drain-mode deduplicates by MessageId before Apply ====================

  /// <summary>
  /// Sharper suspect #1 hypothesis: drain mode's <c>filteredEvents</c> (PerspectiveWorker.cs:902)
  /// does NOT dedupe by <see cref="MessageEnvelope{T}.MessageId"/>. When the upstream
  /// <c>get_stream_events</c> SQL returns the same event multiple times (one row per
  /// <c>perspective_events</c> entry — each with a unique EventWorkId), <c>DeserializeStreamEvents</c>
  /// yields the same envelope twice, and <c>RunWithEventsAsync</c> receives both copies. The
  /// generated runner then calls <c>ApplyEvent</c> once per envelope, producing 2× (or N×) Apply
  /// invocations for the same logical event. Matches the observed exactly-2× doubling symptom.
  /// </summary>
  /// <remarks>
  /// The comment at PerspectiveWorker.cs:837-840 explicitly acknowledges the upstream query
  /// can return duplicates. The downstream Apply path must dedupe.
  /// </remarks>
  [Test]
  public async Task DrainMode_DuplicateEnvelopesFromUpstream_ApplyFiresOncePerEvent_Async() {
    // Arrange — simulate get_stream_events returning the same event twice (as if two
    // perspective_events rows joined to the same event_store row). Both rows carry the same
    // EventId but different EventWorkIds, and DeserializeStreamEvents maps each back to an
    // envelope with the same MessageId.
    var streamId = TrackedGuid.NewMedo().Value;
    var eventId = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "Test.DedupePerspective";
    var runner = new _pathTrackingRunner(Status: PerspectiveProcessingStatus.Completed, AdvanceToEventId: eventId);

    var envelope = new MessageEnvelope<IEvent> {
      MessageId = new MessageId(eventId),
      Payload = new _fakeApplyEvent(1),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    var coordinator = new _dualPathCoordinator {
      StreamIdsToReturnOnce = [streamId],
      // NO PerspectiveWork — we're isolating the drain path.
      PerspectiveWorkToReturnOnce = [],
      StreamEventsToReturn = [
        // Same EventId, two distinct EventWorkIds — mirrors the SQL join returning one row
        // per perspective_events entry.
        new StreamEventData {
          StreamId = streamId,
          EventId = eventId,
          EventType = TypeNameFormatter.Format(typeof(_fakeApplyEvent)),
          EventData = JsonSerializer.Serialize(new _fakeApplyEvent(1)),
          Metadata = null,
          Scope = null,
          EventWorkId = Guid.CreateVersion7()
        },
        new StreamEventData {
          StreamId = streamId,
          EventId = eventId,
          EventType = TypeNameFormatter.Format(typeof(_fakeApplyEvent)),
          EventData = JsonSerializer.Serialize(new _fakeApplyEvent(1)),
          Metadata = null,
          Scope = null,
          EventWorkId = Guid.CreateVersion7()
        }
      ]
    };

    // DeserializeStreamEvents returns one envelope per row; both have the same MessageId.
    var eventStore = new _applyTestEventStore { StreamEnvelopes = { [streamId] = [envelope, envelope] } };
    var registry = new _singleRegistry(runner, perspectiveName, [typeof(_fakeApplyEvent)]);

    // Act
    using var cts = new CancellationTokenSource();
    var (worker, harness) = _createWorker(coordinator, registry, eventStore);
    var workerTask = worker.StartAsync(cts.Token);
    _ = Whizbang.Testing.Workers.WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await coordinator.WaitForCyclesAsync(minCycles: 2, timeout: TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { /* expected */ }

    // Assert — exactly ONE Apply dispatch per (streamId, eventId) despite the upstream duplicate.
    var applyDispatchesForEvent = runner.Invocations
      .Where(i => i.StreamId == streamId && i.PerspectiveName == perspectiveName && i.EventId == eventId)
      .ToList();
    await Assert.That(applyDispatchesForEvent.Count).IsEqualTo(1).Because(
      "Apply-exactly-once contract: drain mode must dedupe by MessageId before dispatching events "
      + "into the runner. Upstream SQL can legitimately return one row per perspective_events "
      + "entry (see PerspectiveWorker.cs:837-840), so the worker must guarantee single-dispatch. "
      + $"Recorded: {string.Join(", ", applyDispatchesForEvent.Select(i => i.Path))}.");
  }

  // ==================== Scenario 1c: mixed-multiplicity burst through drain mode ====================

  /// <summary>
  /// Stronger regression lock-in for the dedupe fix. Three distinct events where each arrives
  /// with a different multiplicity (1×, 2×, 3×) in the upstream drain batch — mirroring a
  /// production stream that has several perspective_events rows for some events but not others.
  /// Each event must produce exactly one Apply dispatch regardless of how many times upstream
  /// returns it.
  /// </summary>
  [Test]
  public async Task DrainMode_MixedMultiplicityBurst_ApplyFiresExactlyOncePerEvent_Async() {
    var streamId = TrackedGuid.NewMedo().Value;
    var eventIdA = TrackedGuid.NewMedo().Value;
    var eventIdB = TrackedGuid.NewMedo().Value;
    var eventIdC = TrackedGuid.NewMedo().Value;
    const string perspectiveName = "Test.MixedMultiplicityPerspective";
    var runner = new _pathTrackingRunner(Status: PerspectiveProcessingStatus.Completed, AdvanceToEventId: eventIdC);

    static MessageEnvelope<IEvent> _envelope(Guid id, int seq) => new() {
      MessageId = new MessageId(id),
      Payload = new _fakeApplyEvent(seq),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    var envelopeA = _envelope(eventIdA, 1);
    var envelopeB = _envelope(eventIdB, 2);
    var envelopeC = _envelope(eventIdC, 3);

    static StreamEventData _raw(Guid streamId, Guid eventId, int seq) => new() {
      StreamId = streamId,
      EventId = eventId,
      EventType = TypeNameFormatter.Format(typeof(_fakeApplyEvent)),
      EventData = JsonSerializer.Serialize(new _fakeApplyEvent(seq)),
      Metadata = null,
      Scope = null,
      EventWorkId = Guid.CreateVersion7()
    };

    var coordinator = new _dualPathCoordinator {
      StreamIdsToReturnOnce = [streamId],
      PerspectiveWorkToReturnOnce = [],
      StreamEventsToReturn = [
        // Event A — 1 row
        _raw(streamId, eventIdA, 1),
        // Event B — 2 rows (duplicate)
        _raw(streamId, eventIdB, 2),
        _raw(streamId, eventIdB, 2),
        // Event C — 3 rows (triple duplicate)
        _raw(streamId, eventIdC, 3),
        _raw(streamId, eventIdC, 3),
        _raw(streamId, eventIdC, 3)
      ]
    };

    // StreamEnvelopes contains the envelopes matching the raw rows — DeserializeStreamEvents
    // will look up by event id and return the same envelope for each duplicate raw row.
    var eventStore = new _applyTestEventStore {
      StreamEnvelopes = { [streamId] = [envelopeA, envelopeB, envelopeC] }
    };
    var registry = new _singleRegistry(runner, perspectiveName, [typeof(_fakeApplyEvent)]);

    using var cts = new CancellationTokenSource();
    var (worker, harness) = _createWorker(coordinator, registry, eventStore);
    var workerTask = worker.StartAsync(cts.Token);
    _ = Whizbang.Testing.Workers.WorkCoordinatorPumpAdapter.RunPumpAsync(coordinator, harness, cts.Token);
    await coordinator.WaitForCyclesAsync(minCycles: 2, timeout: TimeSpan.FromSeconds(10));
    cts.Cancel();
    try { await workerTask; } catch (OperationCanceledException) { /* expected */ }

    foreach (var (eventId, multiplicity) in new[] { (eventIdA, 1), (eventIdB, 2), (eventIdC, 3) }) {
      var count = runner.Invocations.Count(i =>
        i.StreamId == streamId && i.PerspectiveName == perspectiveName && i.EventId == eventId);
      await Assert.That(count).IsEqualTo(1).Because(
        $"Apply-exactly-once: event {eventId:N} appeared {multiplicity}× upstream but must dispatch exactly once. "
        + $"Got {count} dispatches via {string.Join(",", runner.Invocations.Where(i => i.EventId == eventId).Select(i => i.Path))}.");
    }
  }

  // ==================== Scenario 2: IPerspectiveRunner not double-registered ====================

  /// <summary>
  /// Suspect #2 in the plan: the generator or consumer DI code registers the same
  /// <c>IPerspectiveRunner&lt;T&gt;</c> twice, so every <c>ProcessWorkBatchAsync</c> cycle
  /// invokes both registrations.
  /// </summary>
  [Test]
  [Skip("Pending Phase D — add after Phase A RED→GREEN on suspect #1. Requires a registry fake that exposes double-registration and asserts only one runner sees invocations.")]
  public Task DoubleRegisteredPerspectiveRunner_OnlyOneInstanceInvoked_Async() =>
    Task.CompletedTask;

  // ==================== Scenario 3: crash between model save and cursor advance ====================

  /// <summary>
  /// Suspect #3 in the plan: if the model save and cursor advance happen in separate
  /// transactions, a crash between them leaves the model persisted but the cursor
  /// un-advanced. The next cycle re-applies the same events.
  /// </summary>
  [Test]
  [Skip("Pending Phase D — requires IChaosInjector checkpoints wired into PerspectiveWorker (receptor-chaos-scenarios-deferred.md) to simulate crash between model save and cursor advance.")]
  public Task CrashBetweenSaveAndCursorAdvance_DoesNotReApplyEvents_Async() =>
    Task.CompletedTask;

  // ==================== Scenario 4: rewind on populated initialModel ====================

  /// <summary>
  /// Suspect #4 in the plan: <c>RewindAndRunAsync</c> → <c>RunFromModelAsync</c> called
  /// with a populated <c>initialModel</c> AND an incorrect <c>replayFromEventId</c>
  /// re-reads already-applied events and dispatches them through <c>ApplyEvent</c>.
  /// </summary>
  [Test]
  [Skip("Pending Phase D — needs IPerspectiveApplyObserver hook in the generated runner template to count per-event Apply dispatches; proposed in plan but not yet added.")]
  public Task RewindWithPopulatedModel_AppliesOnlyNewEvents_Async() =>
    Task.CompletedTask;

  // ==================== Shared test-double infrastructure ====================

  private sealed record _fakeApplyEvent(int Sequence) : IEvent;

  /// <summary>
  /// Runner that records every path invocation — <c>RunAsync</c>, <c>RunWithEventsAsync</c>,
  /// <c>RewindAndRunAsync</c> — so assertions can verify exactly which path dispatched
  /// which events for which (streamId, perspectiveName) pair.
  /// </summary>
  /// <param name="Status">Status to return from RunAsync / RunWithEventsAsync.</param>
  /// <param name="AdvanceToEventId">Event id to report as LastEventId when Completed.</param>
  private sealed class _pathTrackingRunner(
      PerspectiveProcessingStatus Status,
      Guid AdvanceToEventId) : IPerspectiveRunner {
    public Type PerspectiveType => typeof(object);

    private readonly ConcurrentBag<_Invocation> _invocations = [];

    public IReadOnlyCollection<_Invocation> Invocations => [.. _invocations];

    public Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) {
      _invocations.Add(new _Invocation("RunAsync", streamId, perspectiveName, lastProcessedEventId ?? Guid.Empty));
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Status == PerspectiveProcessingStatus.Completed ? AdvanceToEventId : lastProcessedEventId ?? Guid.Empty,
        Status = Status,
        EventsProcessed = Status == PerspectiveProcessingStatus.Completed ? 1 : 0
      });
    }

    public Task<PerspectiveCursorCompletion> RunWithEventsAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId,
        IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) {
      foreach (var envelope in events) {
        _invocations.Add(new _Invocation("RunWithEventsAsync", streamId, perspectiveName, envelope.MessageId.Value));
      }
      var lastId = events.Count > 0 ? events[^1].MessageId.Value : lastProcessedEventId ?? Guid.Empty;
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = lastId,
        Status = events.Count > 0 ? PerspectiveProcessingStatus.Completed : PerspectiveProcessingStatus.None,
        EventsProcessed = events.Count
      });
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(
        Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) {
      _invocations.Add(new _Invocation("RewindAndRunAsync", streamId, perspectiveName, triggeringEventId));
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = triggeringEventId,
        Status = PerspectiveProcessingStatus.Completed,
        EventsProcessed = 1
      });
    }

    public Task BootstrapSnapshotAsync(
        Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public sealed record _Invocation(string Path, Guid StreamId, string PerspectiveName, Guid EventId);
  }

  /// <summary>
  /// Coordinator that returns a WorkBatch with BOTH PerspectiveStreamIds AND PerspectiveWork
  /// populated for the same stream. Models the production condition the plan calls out as
  /// suspect #1. After one cycle, subsequent polls return an empty batch so the test settles.
  /// </summary>
  private sealed class _dualPathCoordinator : IWorkCoordinator {
    private int _cycleCount;
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _cycleWaiters = new();

    public List<Guid> StreamIdsToReturnOnce { get; set; } = [];
    public List<PerspectiveWork> PerspectiveWorkToReturnOnce { get; set; } = [];
    public List<StreamEventData> StreamEventsToReturn { get; set; } = [];
    public int GetStreamEventsCallCount { get; private set; }

    public Task WaitForCyclesAsync(int minCycles, TimeSpan timeout) {
      var tcs = _cycleWaiters.GetOrAdd(minCycles, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
      return tcs.Task.WaitAsync(timeout);
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default) {
      var current = Interlocked.Increment(ref _cycleCount);
      foreach (var kvp in _cycleWaiters) {
        if (current >= kvp.Key) {
          kvp.Value.TrySetResult();
        }
      }

      if (current == 1) {
        // First cycle returns both, simulating the overlap condition.
        var streamIds = new List<Guid>(StreamIdsToReturnOnce);
        var work = new List<PerspectiveWork>(PerspectiveWorkToReturnOnce);
        return Task.FromResult(new WorkBatch {
          OutboxWork = [],
          InboxWork = [],
          PerspectiveWork = work,
          PerspectiveStreamIds = streamIds
        });
      }

      return Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    }

    public Task<List<StreamEventData>> GetStreamEventsAsync(Guid instanceId, Guid[] streamIds, CancellationToken cancellationToken = default) {
      GetStreamEventsCallCount++;
      return Task.FromResult(new List<StreamEventData>(StreamEventsToReturn));
    }

    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  /// <summary>
  /// Event store that serves pre-configured envelopes for both the drain-mode deserialization
  /// path (<see cref="DeserializeStreamEvents"/>) and the standard-mode read path
  /// (<see cref="ReadPolymorphicAsync"/>). Needed so both dispatch paths can actually run.
  /// </summary>
  private sealed class _applyTestEventStore : IEventStore {
    public ConcurrentDictionary<Guid, List<MessageEnvelope<IEvent>>> StreamEnvelopes { get; } = new();

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(
        IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) {
      var result = new List<MessageEnvelope<IEvent>>();
      foreach (var stream in streamEvents) {
        if (StreamEnvelopes.TryGetValue(stream.StreamId, out var envelopes)) {
          var match = envelopes.FirstOrDefault(e => e.MessageId.Value == stream.EventId);
          if (match is not null) {
            result.Add(match);
          }
        }
      }
      return result;
    }

    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
        Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      if (!StreamEnvelopes.TryGetValue(streamId, out var envelopes)) {
        yield break;
      }
      foreach (var envelope in envelopes) {
        if (fromEventId is null || envelope.MessageId.Value != fromEventId.Value) {
          yield return envelope;
        }
      }
    }

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
        Guid streamId, Guid? afterEventId, Guid upToEventId,
        IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(StreamEnvelopes.TryGetValue(streamId, out var envelopes) ? envelopes.ToList() : []);

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) => _empty<TMessage>(cancellationToken);
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) => _empty<TMessage>(cancellationToken);
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);

    private static async IAsyncEnumerable<MessageEnvelope<T>> _empty<T>([EnumeratorCancellation] CancellationToken ct = default) {
      await Task.CompletedTask;
      yield break;
    }
  }

  private sealed class _singleRegistry(IPerspectiveRunner runner, string perspectiveName, IReadOnlyList<Type> eventTypes) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string name, IServiceProvider serviceProvider) =>
      name == perspectiveName ? runner : null;

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [
      new PerspectiveRegistrationInfo(
        perspectiveName,
        $"global::{perspectiveName}",
        "global::Test.Model",
        [.. eventTypes.Select(TypeNameFormatter.Format)])
    ];

    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class _fakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName { get; } = "ApplyExactlyOnceTestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 12345;
    ServiceInstanceInfo IServiceInstanceProvider.ToInfo() =>
      new() { ServiceName = ServiceName, InstanceId = InstanceId, HostName = HostName, ProcessId = ProcessId };
  }
  private static (PerspectiveWorker Worker, Whizbang.Testing.Workers.PerspectiveWorkerTestHarness Harness) _createWorker(
      IWorkCoordinator coordinator,
      IPerspectiveRunnerRegistry registry,
      IEventStore eventStore) {
    var instanceProvider = new _fakeInstanceProvider();
    var strategy = new InstantCompletionStrategy();
    var harness = new Whizbang.Testing.Workers.PerspectiveWorkerTestHarness();

    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    services.AddSingleton(registry);
    services.AddSingleton<IPerspectiveCompletionStrategy>(strategy);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton(eventStore);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      strategy,
      eventTypeProvider: registry,
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel);
    return (worker, harness);
  }
}
