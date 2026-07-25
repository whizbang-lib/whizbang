using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Phase H step 7 slice 5 — cooldown gate inside the perspective drainer.
/// Pins the decision logic that short-circuits <c>RunWithEventsAsync</c> when every
/// event_work_id is in the <see cref="RecentlyProcessedEventCache"/>. These tests target
/// the static helper directly so the rule can't silently regress under a future drainer
/// refactor — see <see cref="feedback_lock_invariants_in_tests"/>.
/// </summary>
public class CooldownGateDecisionTests {

  private sealed class TestEvent : IEvent {
    public Guid StreamId { get; set; }
  }

  private static MessageEnvelope<IEvent> _envelope(Guid messageId) => new() {
    MessageId = MessageId.From(messageId),
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    Hops = [],
    Payload = new TestEvent()
  };

  private static StreamEventData _raw(Guid eventId, Guid workId, string? perspectiveName = null) => new() {
    StreamId = (Guid)TrackedGuid.NewMedo(),
    EventId = eventId,
    EventType = "TestEvent",
    EventData = "{}",
    EventWorkId = workId,
    Metadata = null,
    Scope = null,
    PerspectiveName = perspectiveName
  };

  private static SystemTimeProvider _provider() =>
    new(new FakeTimeProvider(new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero)));

  [Test]
  public async Task ShouldSkip_NullCache_ReturnsFalseAsync() {
    var eventId = (Guid)TrackedGuid.NewMedo();
    var raw = new[] { _raw(eventId, (Guid)TrackedGuid.NewMedo()) }.ToLookup(r => r.EventId);
    var events = new List<MessageEnvelope<IEvent>> { _envelope(eventId) };

    var skip = PerspectiveWorker._shouldSkipApplyDueToCooldown(events, raw, cache: null);

    await Assert.That(skip).IsFalse();
  }

  [Test]
  public async Task ShouldSkip_EmptyEvents_ReturnsFalseAsync() {
    var cache = new RecentlyProcessedEventCache(_provider());
    var raw = Array.Empty<StreamEventData>().ToLookup(r => r.EventId);

    var skip = PerspectiveWorker._shouldSkipApplyDueToCooldown([], raw, cache);

    await Assert.That(skip).IsFalse();
  }

  [Test]
  public async Task ShouldSkip_AllWorkIdsInCache_ReturnsTrueAsync() {
    var cache = new RecentlyProcessedEventCache(_provider());
    var event1 = (Guid)TrackedGuid.NewMedo();
    var event2 = (Guid)TrackedGuid.NewMedo();
    var work1 = (Guid)TrackedGuid.NewMedo();
    var work2 = (Guid)TrackedGuid.NewMedo();
    cache.MarkProcessed(work1);
    cache.MarkProcessed(work2);

    var raw = new[] { _raw(event1, work1), _raw(event2, work2) }.ToLookup(r => r.EventId);
    var events = new List<MessageEnvelope<IEvent>> { _envelope(event1), _envelope(event2) };

    var skip = PerspectiveWorker._shouldSkipApplyDueToCooldown(events, raw, cache);

    await Assert.That(skip).IsTrue();
  }

  [Test]
  public async Task ShouldSkip_OneFresh_ReturnsFalseAsync() {
    var cache = new RecentlyProcessedEventCache(_provider());
    var event1 = (Guid)TrackedGuid.NewMedo();
    var event2 = (Guid)TrackedGuid.NewMedo();
    var work1 = (Guid)TrackedGuid.NewMedo();
    var work2 = (Guid)TrackedGuid.NewMedo();
    cache.MarkProcessed(work1); // only work1 is cooled

    var raw = new[] { _raw(event1, work1), _raw(event2, work2) }.ToLookup(r => r.EventId);
    var events = new List<MessageEnvelope<IEvent>> { _envelope(event1), _envelope(event2) };

    var skip = PerspectiveWorker._shouldSkipApplyDueToCooldown(events, raw, cache);

    await Assert.That(skip).IsFalse()
      .Because("any fresh event must drop us into the apply path so it gets processed");
  }

  [Test]
  public async Task ShouldSkip_EventMapsToMultipleWorkIds_AllCooled_ReturnsTrueAsync() {
    // Same event_id queued for multiple perspectives → multiple work_ids per event_id in lookup.
    // Every work_id must be cooled for skip to fire.
    var cache = new RecentlyProcessedEventCache(_provider());
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workA = (Guid)TrackedGuid.NewMedo();
    var workB = (Guid)TrackedGuid.NewMedo();
    cache.MarkProcessed(workA);
    cache.MarkProcessed(workB);

    var raw = new[] { _raw(eventId, workA), _raw(eventId, workB) }.ToLookup(r => r.EventId);
    var events = new List<MessageEnvelope<IEvent>> { _envelope(eventId) };

    var skip = PerspectiveWorker._shouldSkipApplyDueToCooldown(events, raw, cache);

    await Assert.That(skip).IsTrue();
  }

  [Test]
  public async Task ShouldSkip_EventMapsToMultipleWorkIds_OneFresh_ReturnsFalseAsync() {
    var cache = new RecentlyProcessedEventCache(_provider());
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workA = (Guid)TrackedGuid.NewMedo();
    var workB = (Guid)TrackedGuid.NewMedo();
    cache.MarkProcessed(workA); // only workA cooled, workB fresh

    var raw = new[] { _raw(eventId, workA), _raw(eventId, workB) }.ToLookup(r => r.EventId);
    var events = new List<MessageEnvelope<IEvent>> { _envelope(eventId) };

    var skip = PerspectiveWorker._shouldSkipApplyDueToCooldown(events, raw, cache);

    await Assert.That(skip).IsFalse();
  }

  // ======================================================================
  // PER-PERSPECTIVE COOLDOWN ISOLATION (silent-skip bug)
  // ======================================================================
  // Bug: when an event_id has multiple wh_perspective_events rows (one per registered
  // perspective), `rawByEventId[eventId]` returns ALL of them. Marking any one perspective's
  // work_id as cooled would also short-circuit OTHER perspectives' Apply on subsequent drains
  // — because the existing logic iterated the entire raw collection regardless of which
  // perspective was being asked. Production symptom: Order got data, but its OrderLine
  // perspectives (line items, attributes, notes, etc.) all stayed empty after order creation.
  //
  // Fix: pass `perspectiveName` to the cooldown helpers; filter `rawByEventId[eventId]` to
  // only entries whose `PerspectiveName` matches the current perspective.
  //
  // RED-first: these tests fail against the pre-fix code that treated all raw rows uniformly.

  private const string PERSP_A = "MyApp.Domain.PerspectiveA+Projection";
  private const string PERSP_B = "MyApp.Domain.PerspectiveB+Projection";

  [Test]
  public async Task ShouldSkip_PerspectiveAOnlyCooled_DraftingPerspectiveB_ReturnsFalseAsync() {
    // Reproduction of a multi-perspective fanout silent-skip observed in production.
    // Setup: ONE event_id, TWO perspectives (A + B). A's work_id is cooled; B's isn't.
    // Expected (post-fix): drain for B returns false (apply must run for B).
    // Pre-fix: returns true because the loop sees A's work_id cooled and the bug treats it
    // as proof that "every work_id for this event is cooled" — incorrectly skipping B.
    var cache = new RecentlyProcessedEventCache(_provider());
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workForA = (Guid)TrackedGuid.NewMedo();
    var workForB = (Guid)TrackedGuid.NewMedo();
    cache.MarkProcessed(workForA);  // only A's work is cooled

    var raw = new[] {
      _raw(eventId, workForA, PERSP_A),
      _raw(eventId, workForB, PERSP_B),
    }.ToLookup(r => r.EventId);
    var events = new List<MessageEnvelope<IEvent>> { _envelope(eventId) };

    var skip = PerspectiveWorker._shouldSkipApplyDueToCooldown(events, raw, cache, PERSP_B);

    await Assert.That(skip).IsFalse()
      .Because("B's work_id is fresh — A's cooled state is irrelevant to B's drain decision. The pre-fix code sees A's cooled work and incorrectly returns true, which is the silent-skip bug.");
  }

  [Test]
  public async Task ShouldSkip_PerspectiveBCooled_DraftingPerspectiveB_ReturnsTrueAsync() {
    // Symmetry check: when B's own work IS cooled, drain for B correctly skips.
    // This guards against an over-zealous fix that would always return false.
    var cache = new RecentlyProcessedEventCache(_provider());
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workForA = (Guid)TrackedGuid.NewMedo();
    var workForB = (Guid)TrackedGuid.NewMedo();
    cache.MarkProcessed(workForB);  // B is cooled

    var raw = new[] {
      _raw(eventId, workForA, PERSP_A),
      _raw(eventId, workForB, PERSP_B),
    }.ToLookup(r => r.EventId);
    var events = new List<MessageEnvelope<IEvent>> { _envelope(eventId) };

    var skip = PerspectiveWorker._shouldSkipApplyDueToCooldown(events, raw, cache, PERSP_B);

    await Assert.That(skip).IsTrue()
      .Because("B's own work is cooled, so its apply path should short-circuit.");
  }

  [Test]
  public async Task MarkProcessed_PerspectiveA_DoesNotMarkPerspectiveBsWorkIdAsync() {
    // Reproduction of the cooldown-cache poisoning. After A's apply succeeds and we mark
    // its work as processed, B's work_id (under the same event_id but different perspective)
    // MUST NOT be marked. Pre-fix: both A's and B's work_ids end up in the cache, and the
    // next drain for B incorrectly skips Apply.
    var cache = new RecentlyProcessedEventCache(_provider());
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workForA = (Guid)TrackedGuid.NewMedo();
    var workForB = (Guid)TrackedGuid.NewMedo();

    var raw = new[] {
      _raw(eventId, workForA, PERSP_A),
      _raw(eventId, workForB, PERSP_B),
    }.ToLookup(r => r.EventId);
    var events = new List<MessageEnvelope<IEvent>> { _envelope(eventId) };

    PerspectiveWorker._markProcessedInCooldown(cache, events, raw, PERSP_A);

    await Assert.That(cache.WasRecentlyProcessed(workForA)).IsTrue()
      .Because("A's own work must be marked when A finishes apply.");
    await Assert.That(cache.WasRecentlyProcessed(workForB)).IsFalse()
      .Because("B's work must NOT be marked when A finishes apply — that would cause B's next drain to incorrectly skip Apply (the silent-skip bug).");
  }

  [Test]
  public async Task MarkProcessed_NoPerspectiveSupplied_RetainsLegacyAllRowsBehaviorAsync() {
    // Back-compat: when callers don't supply a perspective name, every raw row under the
    // event_id is marked. This preserves the pre-fix behavior for legacy/test paths.
    var cache = new RecentlyProcessedEventCache(_provider());
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workA = (Guid)TrackedGuid.NewMedo();
    var workB = (Guid)TrackedGuid.NewMedo();

    var raw = new[] {
      _raw(eventId, workA, perspectiveName: null),
      _raw(eventId, workB, perspectiveName: null),
    }.ToLookup(r => r.EventId);
    var events = new List<MessageEnvelope<IEvent>> { _envelope(eventId) };

    PerspectiveWorker._markProcessedInCooldown(cache, events, raw, perspectiveName: null);

    await Assert.That(cache.WasRecentlyProcessed(workA)).IsTrue();
    await Assert.That(cache.WasRecentlyProcessed(workB)).IsTrue();
  }

  [Test]
  public async Task ShouldSkip_AllPerspectivesShareEventId_OnlyMyPerspectiveCooled_ReturnsTrueAsync() {
    // The full scenario: 3 perspectives subscribe to one event_id. My perspective (B)'s
    // own work IS cooled. The other two perspectives' work_ids are also in the cache (from
    // their own apply success). Drain for B should skip — its OWN work is cooled.
    var cache = new RecentlyProcessedEventCache(_provider());
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workA = (Guid)TrackedGuid.NewMedo();
    var workB = (Guid)TrackedGuid.NewMedo();
    var workC = (Guid)TrackedGuid.NewMedo();
    cache.MarkProcessed(workA);
    cache.MarkProcessed(workB);
    cache.MarkProcessed(workC);

    var raw = new[] {
      _raw(eventId, workA, PERSP_A),
      _raw(eventId, workB, PERSP_B),
      _raw(eventId, workC, "MyApp.PerspectiveC+Projection"),
    }.ToLookup(r => r.EventId);
    var events = new List<MessageEnvelope<IEvent>> { _envelope(eventId) };

    var skip = PerspectiveWorker._shouldSkipApplyDueToCooldown(events, raw, cache, PERSP_B);

    await Assert.That(skip).IsTrue();
  }

  [Test]
  public async Task ShouldSkip_EnvelopeMessageIdNotInRawLookup_ReturnsFalseAsync() {
    // Reproduces the production bug: when filteredEvents contains an envelope whose
    // MessageId.Value isn't a key in rawByEventId, the inner foreach loops zero times.
    // The pre-fix implementation falls through to `return true` — incorrectly telling the
    // drainer to skip a never-applied event. This is what masked drain-mode dispatch in
    // production (saga events) and what made DrainModeAndStandardMode_DoNotBothFireForSameStream
    // fail: drain returns early without populating batchProcessedEvents, the standard-mode
    // guard doesn't fire, and neither path applies the event.
    var cache = new RecentlyProcessedEventCache(_provider());
    var unrelatedEventId = (Guid)TrackedGuid.NewMedo();
    var unrelatedWorkId = (Guid)TrackedGuid.NewMedo();
    cache.MarkProcessed(unrelatedWorkId);

    // Lookup contains an UNRELATED entry — the envelope's MessageId is NOT a key.
    var raw = new[] { _raw(unrelatedEventId, unrelatedWorkId) }.ToLookup(r => r.EventId);

    var orphanEventId = (Guid)TrackedGuid.NewMedo();
    var events = new List<MessageEnvelope<IEvent>> { _envelope(orphanEventId) };

    var skip = PerspectiveWorker._shouldSkipApplyDueToCooldown(events, raw, cache);

    await Assert.That(skip).IsFalse()
      .Because("If we cannot determine the event's work_id mapping, we MUST default to applying — silent skip strands the event forever and prevents PostAllPerspectives from firing.");
  }

  [Test]
  public async Task ShouldSkip_OneEnvelopeMappedAndOneNotInLookup_ReturnsFalseAsync() {
    // Mixed case: one envelope has its raw row in the lookup (cooled), another envelope
    // has no entries at all. Pre-fix: the second envelope contributes nothing to the
    // decision (zero inner-loop iterations) and the first one's "cooled" state alone
    // returns true. Post-fix: the missing-mapping envelope MUST default to false.
    var cache = new RecentlyProcessedEventCache(_provider());
    var mappedEventId = (Guid)TrackedGuid.NewMedo();
    var mappedWorkId = (Guid)TrackedGuid.NewMedo();
    cache.MarkProcessed(mappedWorkId);

    var raw = new[] { _raw(mappedEventId, mappedWorkId) }.ToLookup(r => r.EventId);
    var orphanEventId = (Guid)TrackedGuid.NewMedo();
    var events = new List<MessageEnvelope<IEvent>> {
      _envelope(mappedEventId),   // mapped + cooled
      _envelope(orphanEventId),   // not in lookup — pre-fix bug treats this as "skip OK"
    };

    var skip = PerspectiveWorker._shouldSkipApplyDueToCooldown(events, raw, cache);

    await Assert.That(skip).IsFalse()
      .Because("Any envelope whose raw mapping is unknown must force the apply path, even if other envelopes appear cooled.");
  }

  // ---------- Cooldown skip: lifecycle completion signaling ----------
  // The bug: when cooldown skip fires (apply already ran in a previous drain), the cooldown
  // path used to bare-`return` from the drain helper. That left two pieces of lifecycle state
  // unsignaled:
  //   1. ILifecycleCoordinator.SignalPerspectiveComplete — without it,
  //      AreAllPerspectivesComplete returns false forever for events with multiple perspective
  //      expectations. PostAllPerspectivesInline never fires. [FireAt(PostAllPerspectivesInline)]
  //      receptors never run. Saga completion handlers (e.g., OrderFieldPopulation) never
  //      emit SagaCompletedEvent. Production loaders waiting on saga-name notifications hang
  //      forever — repro'd in production against a consumer's services.
  //   2. DrainBatchContext.BatchProcessedEvents — the per-batch dict that drives the
  //      "fire PostAllPerspectives + PostLifecycle" foreach in PerspectiveWorker's batch
  //      finalize loop. Cooldown-skipped events were absent from it, so even if the
  //      WhenAll gate happened to be satisfied by side channels, the foreach skipped them.
  //
  // The previous apply that populated the cooldown DID signal completion at that time, but
  // that signal occurred in a different DrainBatchContext (different batch). Tracking is
  // event-keyed, not batch-keyed, so re-signaling on a later drain is idempotent and safe
  // (LifecycleCoordinator.SignalPerspectiveComplete uses a HashSet-style state). The fix is
  // explicit: when we skip apply via cooldown, run the same completion-signaling that the
  // apply success path runs (minus PostPerspective lifecycle, which already fired during the
  // first apply).

  private sealed class CapturingLifecycleCoordinator : ILifecycleCoordinator {
    public ConcurrentBag<(Guid EventId, string Perspective)> Signaled { get; } = [];
    public bool SignalPerspectiveComplete(Guid eventId, string perspectiveName) {
      Signaled.Add((eventId, perspectiveName));
      return false;
    }
    public ILifecycleTracking BeginTracking(Guid eventId, IMessageEnvelope envelope, LifecycleStage entryStage, MessageSource source, Guid? streamId = null, Type? perspectiveType = null)
      => throw new NotImplementedException();
    public ILifecycleTracking? GetTracking(Guid eventId) => null;
    public void ExpectCompletionsFrom(Guid eventId, params PostLifecycleCompletionSource[] sources) { }
    public ValueTask SignalSegmentCompleteAsync(Guid eventId, PostLifecycleCompletionSource source, IServiceProvider scopedProvider, CancellationToken ct) => ValueTask.CompletedTask;
    public void AbandonTracking(Guid eventId) { }
    public void ExpectPerspectiveCompletions(Guid eventId, IReadOnlyList<string> perspectiveNames) { }
    public bool AreAllPerspectivesComplete(Guid eventId) => false;
    public int CleanupStaleTracking(TimeSpan inactivityThreshold) => 0;
  }

  [Test]
  public async Task SignalCooldownSkippedEvents_FillsBatchProcessedEvents_AndSignalsCompletionAsync() {
    // RED-first regression lock for a production hang. When cooldown skip
    // fires, PerspectiveWorker MUST still: (1) add each filtered event to BatchProcessedEvents
    // so the batch finalize loop can fire PostAllPerspectives; (2) signal perspective complete
    // on the lifecycle coordinator so AreAllPerspectivesComplete becomes true. Pre-fix the
    // drain helper just `return`ed, leaving both unsignaled — saga handlers never fired and
    // SagaCompletedEvent was never emitted.
    var coordinator = new CapturingLifecycleCoordinator();
    var batchProcessed = new ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)>();
    var streamId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "TestPerspective";
    var events = new List<MessageEnvelope<IEvent>> {
      _envelope((Guid)TrackedGuid.NewMedo()),
      _envelope((Guid)TrackedGuid.NewMedo()),
    };

    PerspectiveWorker._signalCooldownSkippedEvents(events, perspectiveName, streamId, batchProcessed, coordinator);

    await Assert.That(batchProcessed.Count).IsEqualTo(2)
      .Because("BatchProcessedEvents must include cooldown-skipped events so the batch foreach fires PostAllPerspectives for them.");
    foreach (var envelope in events) {
      await Assert.That(batchProcessed.ContainsKey(envelope.MessageId.Value)).IsTrue();
    }
    await Assert.That(coordinator.Signaled.Count).IsEqualTo(2)
      .Because("Each cooldown-skipped envelope must signal perspective completion so AreAllPerspectivesComplete can become true.");
    foreach (var (eventId, name) in coordinator.Signaled) {
      await Assert.That(name).IsEqualTo(perspectiveName);
      await Assert.That(events.Any(e => e.MessageId.Value == eventId)).IsTrue();
    }
  }

  [Test]
  public async Task SignalCooldownSkippedEvents_NullCoordinator_StillFillsBatchProcessedAsync() {
    // Coordinator is optional in DI (test harnesses, simple consumers). Skipping it must not
    // throw — and BatchProcessedEvents still gets populated so the WhenAll-disabled path
    // (no expectations registered) lets PostAllPerspectives fire via the always-true gate.
    var batchProcessed = new ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)>();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var events = new List<MessageEnvelope<IEvent>> { _envelope((Guid)TrackedGuid.NewMedo()) };

    PerspectiveWorker._signalCooldownSkippedEvents(events, "TestPerspective", streamId, batchProcessed, lifecycleCoordinator: null);

    await Assert.That(batchProcessed.Count).IsEqualTo(1);
  }

  [Test]
  public async Task SignalCooldownSkippedEvents_EmptyEvents_NoOpAsync() {
    var coordinator = new CapturingLifecycleCoordinator();
    var batchProcessed = new ConcurrentDictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)>();

    PerspectiveWorker._signalCooldownSkippedEvents([], "TestPerspective", (Guid)TrackedGuid.NewMedo(), batchProcessed, coordinator);

    await Assert.That(batchProcessed.Count).IsEqualTo(0);
    await Assert.That(coordinator.Signaled.Count).IsEqualTo(0);
  }
}
