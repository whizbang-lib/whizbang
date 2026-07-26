using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Phase H step 6 slice 4 regression locks for the cursor-inversion detector.
/// PerspectiveWorker._findCursorInversionAnchor decides between forward-apply
/// (RunWithEventsAsync) and rewind (RewindAndRunAsync) based on whether any
/// pending event has event_id ≤ cached cursor.
/// </summary>
public class CursorInversionDetectorTests {

  // --- helpers ---

  private sealed class TestEvent : IEvent {
    public Guid StreamId { get; set; }
  }

  private static MessageEnvelope<IEvent> _envelope(Guid messageId) => new() {
    MessageId = MessageId.From(messageId),
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    Hops = [],
    Payload = new TestEvent()
  };

  private static Guid _uuidv7() => (Guid)TrackedGuid.NewMedo();

  // --- tests ---

  [Test]
  public async Task FindCursorInversionAnchor_AllEventsAfterCursor_ReturnsNullAsync() {
    // Normal forward-apply path: cursor at A, pending events all > A.
    var cursor = _uuidv7();
    await Task.Delay(2);
    var newer1 = _uuidv7();
    await Task.Delay(2);
    var newer2 = _uuidv7();

    var events = new List<MessageEnvelope<IEvent>> { _envelope(newer1), _envelope(newer2) };
    var anchor = PerspectiveWorker._findCursorInversionAnchor(events, cursor);
    await Assert.That(anchor).IsNull()
      .Because("all pending events newer than cursor — no inversion, normal forward apply");
  }

  [Test]
  public async Task FindCursorInversionAnchor_OnePendingBeforeCursor_ReturnsThatEventAsync() {
    // Inversion: cursor at C, pending events include B (< C) and D (> C).
    // Anchor = B (the violator).
    var older = _uuidv7();
    await Task.Delay(2);
    var cursor = _uuidv7();
    await Task.Delay(2);
    var newer = _uuidv7();

    var events = new List<MessageEnvelope<IEvent>> { _envelope(older), _envelope(newer) };
    var anchor = PerspectiveWorker._findCursorInversionAnchor(events, cursor);
    await Assert.That(anchor).IsEqualTo(older)
      .Because("the older event (event_id < cursor) is the inversion violator and rewind anchor");
  }

  [Test]
  public async Task FindCursorInversionAnchor_TwoPendingBeforeCursor_ReturnsEarliestAsync() {
    // Multiple violators: anchor = earliest. Picking any later one would risk a
    // snapshot already past one of the other violators.
    var oldest = _uuidv7();
    await Task.Delay(2);
    var older = _uuidv7();
    await Task.Delay(2);
    var cursor = _uuidv7();
    await Task.Delay(2);
    var newer = _uuidv7();

    var events = new List<MessageEnvelope<IEvent>> { _envelope(older), _envelope(oldest), _envelope(newer) };
    var anchor = PerspectiveWorker._findCursorInversionAnchor(events, cursor);
    await Assert.That(anchor).IsEqualTo(oldest)
      .Because("when multiple pending events fall below the cursor, the EARLIEST is the rewind anchor");
  }

  [Test]
  public async Task FindCursorInversionAnchor_EventEqualsCursor_NotInversionAsync() {
    // Boundary case revised after a production over-trigger incident:
    // pending event_id == cursor is the EXPECTED state during the cursor-flush window.
    // The runner just applied this event, advancing the cursor synchronously, but the
    // wh_perspective_events row's completion (DELETE in prod / processed_at in debug)
    // is async via the completion flusher (~10ms coalesce). The runner's idempotency
    // filter inside RunWithEventsAsync handles the duplicate cleanly. Triggering rewind
    // here caused a hot loop of full replays on busy streams.
    var cursor = _uuidv7();
    var events = new List<MessageEnvelope<IEvent>> { _envelope(cursor) };
    var anchor = PerspectiveWorker._findCursorInversionAnchor(events, cursor);
    await Assert.That(anchor).IsNull()
      .Because("event_id == cursor is normal cursor-flush lag, not inversion");
  }

  [Test]
  public async Task FindCursorInversionAnchor_OneEqualOneOlder_AnchorsOnTheStrictlyOlderAsync() {
    // Mixed case: the equal one is benign cursor-lag; the strictly-older one IS a real
    // inversion. Anchor must be the strictly-older event, not the equal one.
    var older = _uuidv7();
    await Task.Delay(2);
    var cursor = _uuidv7();

    var events = new List<MessageEnvelope<IEvent>> { _envelope(cursor), _envelope(older) };
    var anchor = PerspectiveWorker._findCursorInversionAnchor(events, cursor);
    await Assert.That(anchor).IsEqualTo(older);
  }

  [Test]
  public async Task FindCursorInversionAnchor_CursorEmpty_ReturnsNullAsync() {
    // Cold-start / new-perspective case: cursor null/empty means no inversion is possible
    // by definition — every event is >= empty cursor in the time-ordered space.
    var anchor = PerspectiveWorker._findCursorInversionAnchor(
      [_envelope(_uuidv7()), _envelope(_uuidv7())],
      Guid.Empty);
    await Assert.That(anchor).IsNull()
      .Because("Guid.Empty cursor means cold-start — no inversion check applies");
  }

  [Test]
  public async Task FindCursorInversionAnchor_EmptyEventsList_ReturnsNullAsync() {
    // Defensive: nothing pending = nothing to invert.
    var cursor = _uuidv7();
    var anchor = PerspectiveWorker._findCursorInversionAnchor([], cursor);
    await Assert.That(anchor).IsNull();
  }

  // ---------- _resolveInversionAnchor (slice 26.13) ----------
  //
  // Routes between commit_sequence and event_id detectors. When commit_sequence cursor is
  // available, that detector's null return is FINAL — no fall-through to the UUIDv7 path.
  // Without this rule, same-millisecond events whose UUIDv7 lex order disagrees with commit
  // order produce false-positive rewinds (a production regression, thousands of logged inversions).

  private static StreamEventData _raw(Guid streamId, Guid eventId, long? commitSequence) => new() {
    StreamId = streamId,
    EventId = eventId,
    EventType = "T",
    EventData = "{}",
    EventWorkId = (Guid)TrackedGuid.NewMedo(),
    CommitSequence = commitSequence,
  };

  private static ILookup<Guid, StreamEventData> _lookup(params StreamEventData[] rows) =>
    rows.ToLookup(r => r.EventId);

  private static Whizbang.Core.SystemTimeProvider _timeProvider() =>
    new(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
      new DateTimeOffset(2026, 5, 24, 12, 0, 0, TimeSpan.Zero)));

  [Test]
  public async Task ResolveInversionAnchor_CommitSequenceCursorPresent_UUIDv7ReversedButCommitOrderCorrect_ReturnsNullAsync() {
    // THE bug scenario from a production run (same-millisecond stream events):
    // Three same-millisecond UUIDv7 events whose random suffix put them in one lex order
    // but commit_sequence puts them in another. Cursor advanced to the highest-commit_seq
    // event; a pending sibling has a lower UUIDv7 lex value but a HIGHER commit_seq.
    // commit_sequence detector says "no inversion" → must NOT fall back to event_id detector.
    var streamId = Guid.NewGuid();

    // UUIDv7-lex-order: cursorLowLex < pendingLex — would trip the event_id detector.
    // Commit-order: cursorLowLex (cseq=100) < pendingLex (cseq=101) — no inversion.
    var cursorEventId = Guid.Parse("0198a1b2-c3d4-77b4-bf8d-62ba6ca3d5c4");
    var pendingEventId = Guid.Parse("0198a1b2-c3d1-74e7-bf97-e2ef18941879");

    var pendingEnvelope = _envelope(pendingEventId);
    var rawLookup = _lookup(_raw(streamId, pendingEventId, commitSequence: 101));

    var anchor = PerspectiveWorker._resolveInversionAnchor(
      filteredEvents: [pendingEnvelope],
      rawByEventId: rawLookup,
      lastProcessedEventId: cursorEventId,
      lastProcessedCommitSequence: 100L);

    await Assert.That(anchor).IsNull()
      .Because("commit_sequence cursor present + pending event's commit_seq > cursor → no inversion; event_id fallback must NOT fire");
  }

  [Test]
  public async Task ResolveInversionAnchor_CommitSequenceCursorPresent_PendingEqualsCursor_ReturnsNullAsync() {
    // Cursor-flush race: cursor advanced to event X (commit_seq 287962), but the same X is
    // still in the pending queue because PerspectiveCompletionFlushWorker hasn't deleted the
    // perspective_events row yet. Same commit_sequence ≠ inversion — it's the SAME event,
    // idempotent re-drain. The runner template's filter handles it without a rewind.
    // Strict `<` semantics — observed in a production run on OrderRemoteWork.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var cursorEventId = (Guid)TrackedGuid.NewMedo();
    var sameEventPending = cursorEventId;

    var rawLookup = _lookup(_raw(streamId, sameEventPending, commitSequence: 287962));

    var anchor = PerspectiveWorker._resolveInversionAnchor(
      filteredEvents: [_envelope(sameEventPending)],
      rawByEventId: rawLookup,
      lastProcessedEventId: cursorEventId,
      lastProcessedCommitSequence: 287962L);

    await Assert.That(anchor).IsNull()
      .Because("pending commit_seq == cursor commit_seq means same event (cursor-flush race), not inversion");
  }

  [Test]
  public async Task ResolveInversionAnchor_CommitSequenceCursorPresent_ActualCommitOrderViolation_ReturnsViolatorAsync() {
    // Real inversion via commit_sequence: pending event has lower commit_seq than cursor.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var cursorEventId = (Guid)TrackedGuid.NewMedo();
    var pendingEventId = (Guid)TrackedGuid.NewMedo();

    var rawLookup = _lookup(_raw(streamId, pendingEventId, commitSequence: 50));

    var anchor = PerspectiveWorker._resolveInversionAnchor(
      filteredEvents: [_envelope(pendingEventId)],
      rawByEventId: rawLookup,
      lastProcessedEventId: cursorEventId,
      lastProcessedCommitSequence: 100L);

    await Assert.That(anchor).IsEqualTo(pendingEventId)
      .Because("pending commit_seq (50) ≤ cursor commit_seq (100) is a real inversion");
  }

  [Test]
  public async Task ResolveInversionAnchor_NoCommitSequenceCursor_FallsBackToEventIdDetectorAsync() {
    // Pre-slice-26 cursor (no commit_sequence in cache, e.g. legacy row before stamper landed).
    // Must fall back to event_id detector for SOME inversion protection. Imperfect (UUIDv7
    // false positives possible) but better than no protection at all.
    var older = _uuidv7();
    await Task.Delay(2);
    var cursor = _uuidv7();
    await Task.Delay(2);
    var newer = _uuidv7();

    var anchor = PerspectiveWorker._resolveInversionAnchor(
      filteredEvents: [_envelope(older), _envelope(newer)],
      rawByEventId: _lookup(),  // empty — irrelevant since commit_sequence path is skipped
      lastProcessedEventId: cursor,
      lastProcessedCommitSequence: null);

    await Assert.That(anchor).IsEqualTo(older)
      .Because("no commit_sequence cursor → fall back to event_id detector → older violator returned");
  }

  [Test]
  public async Task ResolveInversionAnchor_NoCursorAtAll_ReturnsNullAsync() {
    // Cold-start / brand-new perspective. Nothing to compare against; no inversion possible.
    var anchor = PerspectiveWorker._resolveInversionAnchor(
      filteredEvents: [_envelope(_uuidv7())],
      rawByEventId: _lookup(),
      lastProcessedEventId: null,
      lastProcessedCommitSequence: null);

    await Assert.That(anchor).IsNull();
  }

  [Test]
  public async Task ResolveInversionAnchor_NoCommitSeqCursor_PendingStamped_SkipsEventIdFallbackAsync() {
    // Slice 26.18 — when cursor cache has no commit_sequence (stamper lag at cursor advance
    // time) but pending events ARE stamped, comparing event_id-vs-event_id can produce false
    // positives (UUIDv7 generation-vs-commit timing race). Skip the fallback entirely so we
    // don't trigger spurious rewinds. The runner template's idempotency filter handles
    // already-applied events safely at apply time.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var older = _uuidv7();   // pending — lex-less than cursor
    await Task.Delay(2);
    var cursor = _uuidv7();

    var rawLookup = _lookup(_raw(streamId, older, commitSequence: 1000));

    var anchor = PerspectiveWorker._resolveInversionAnchor(
      filteredEvents: [_envelope(older)],
      rawByEventId: rawLookup,
      lastProcessedEventId: cursor,
      lastProcessedCommitSequence: null);

    await Assert.That(anchor).IsNull()
      .Because("commit_sequence cursor missing + pending event stamped → unsafe to compare; skip detection");
  }

  [Test]
  public async Task ResolveInversionAnchor_NoCommitSeqCursor_NoneStamped_FallsBackToEventIdAsync() {
    // When NEITHER side has commit_sequence (pre-slice-26 legacy data, or stamper is way
    // behind), event_id detector is still the only signal we have — preserve that behavior.
    var older = _uuidv7();
    await Task.Delay(2);
    var cursor = _uuidv7();

    var anchor = PerspectiveWorker._resolveInversionAnchor(
      filteredEvents: [_envelope(older)],
      rawByEventId: _lookup(),  // no stamped raw rows
      lastProcessedEventId: cursor,
      lastProcessedCommitSequence: null);

    await Assert.That(anchor).IsEqualTo(older)
      .Because("nothing is stamped on either side → event_id fallback is the only option");
  }

  // ---------- _partitionByCooldown (slice 26.15) ----------
  //
  // Splits the drain batch into (cooled, fresh) so the inversion detector runs only on
  // the truly-pending events. Without this, a mixed batch (some events still warm in the
  // cooldown cache while their perspective_events rows haven't been DELETEd yet, others
  // genuinely new) made cooldown's "all-or-nothing" gate return false, letting the cooled
  // events look like real inversions to the detector and triggering ~1100 spurious rewinds
  // per bulk-import run.

  [Test]
  public async Task PartitionByCooldown_AllCooled_AllInCooledListAsync() {
    var cache = new Whizbang.Core.Workers.RecentlyProcessedEventCache(_timeProvider());
    var streamId = (Guid)TrackedGuid.NewMedo();
    var e1 = (Guid)TrackedGuid.NewMedo();
    var w1 = (Guid)TrackedGuid.NewMedo();
    var e2 = (Guid)TrackedGuid.NewMedo();
    var w2 = (Guid)TrackedGuid.NewMedo();

    cache.MarkProcessed(w1);
    cache.MarkProcessed(w2);

    var (cooled, fresh) = PerspectiveWorker._partitionByCooldown(
      [_envelope(e1), _envelope(e2)],
      _lookup(_raw(streamId, e1, 1L) with { EventWorkId = w1, PerspectiveName = "P" },
              _raw(streamId, e2, 2L) with { EventWorkId = w2, PerspectiveName = "P" }),
      cache,
      perspectiveName: "P");

    await Assert.That(cooled.Count).IsEqualTo(2);
    await Assert.That(fresh.Count).IsEqualTo(0);
  }

  [Test]
  public async Task PartitionByCooldown_NoneCooled_AllInFreshListAsync() {
    var cache = new Whizbang.Core.Workers.RecentlyProcessedEventCache(_timeProvider());
    var streamId = (Guid)TrackedGuid.NewMedo();
    var e1 = (Guid)TrackedGuid.NewMedo();
    var e2 = (Guid)TrackedGuid.NewMedo();

    var (cooled, fresh) = PerspectiveWorker._partitionByCooldown(
      [_envelope(e1), _envelope(e2)],
      _lookup(_raw(streamId, e1, 1L) with { PerspectiveName = "P" },
              _raw(streamId, e2, 2L) with { PerspectiveName = "P" }),
      cache,
      perspectiveName: "P");

    await Assert.That(cooled.Count).IsEqualTo(0);
    await Assert.That(fresh.Count).IsEqualTo(2);
  }

  [Test]
  public async Task PartitionByCooldown_MixedCooledAndFresh_SplitsCorrectlyAsync() {
    // The saga-batch race: e1 was just applied (still warm in cooldown, row not yet
    // DELETEd), e2 is the next batch's fresh event. Pre-26.15 cooldown returned false
    // (not all cooled) → inversion detector saw e1 as "pending ≤ cursor" → spurious rewind.
    var cache = new Whizbang.Core.Workers.RecentlyProcessedEventCache(_timeProvider());
    var streamId = (Guid)TrackedGuid.NewMedo();
    var e1 = (Guid)TrackedGuid.NewMedo();
    var w1 = (Guid)TrackedGuid.NewMedo();
    var e2 = (Guid)TrackedGuid.NewMedo();

    cache.MarkProcessed(w1);

    var (cooled, fresh) = PerspectiveWorker._partitionByCooldown(
      [_envelope(e1), _envelope(e2)],
      _lookup(_raw(streamId, e1, 1L) with { EventWorkId = w1, PerspectiveName = "P" },
              _raw(streamId, e2, 2L) with { PerspectiveName = "P" }),
      cache,
      perspectiveName: "P");

    await Assert.That(cooled.Count).IsEqualTo(1)
      .Because("e1 is in the cooldown cache → cooled");
    await Assert.That(cooled[0].MessageId.Value).IsEqualTo(e1);
    await Assert.That(fresh.Count).IsEqualTo(1)
      .Because("e2 hasn't been processed yet → fresh");
    await Assert.That(fresh[0].MessageId.Value).IsEqualTo(e2);
  }

  [Test]
  public async Task PartitionByCooldown_NullCache_AllFreshAsync() {
    // When cooldown is disabled (null cache), everything must go through normal apply.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var e1 = (Guid)TrackedGuid.NewMedo();

    var (cooled, fresh) = PerspectiveWorker._partitionByCooldown(
      [_envelope(e1)],
      _lookup(_raw(streamId, e1, 1L) with { PerspectiveName = "P" }),
      cache: null,
      perspectiveName: "P");

    await Assert.That(cooled.Count).IsEqualTo(0);
    await Assert.That(fresh.Count).IsEqualTo(1);
  }

  [Test]
  public async Task PartitionByCooldown_EnvelopeMissingFromRawLookup_TreatedAsFreshAsync() {
    // Defensive: if rawByEventId has no row for an envelope (mapping mismatch), don't
    // accidentally treat it as cooled. Default to fresh so apply runs (matches
    // _shouldSkipApplyDueToCooldown's `rawSeen` guard).
    var cache = new Whizbang.Core.Workers.RecentlyProcessedEventCache(_timeProvider());
    var e1 = (Guid)TrackedGuid.NewMedo();

    var (cooled, fresh) = PerspectiveWorker._partitionByCooldown(
      [_envelope(e1)],
      _lookup(),  // empty lookup
      cache,
      perspectiveName: "P");

    await Assert.That(cooled.Count).IsEqualTo(0);
    await Assert.That(fresh.Count).IsEqualTo(1);
  }
}
