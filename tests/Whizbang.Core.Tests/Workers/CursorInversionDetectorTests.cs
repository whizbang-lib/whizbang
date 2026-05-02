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
    // Boundary case revised after a consumer BFF over-trigger incident (2026-05-02 ~09:43):
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
}
