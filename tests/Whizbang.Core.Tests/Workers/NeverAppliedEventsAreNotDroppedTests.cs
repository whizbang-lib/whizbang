// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <para>Two components decide what happens to a pending event, and they read different marks.
/// The worker's inversion detector reads the perspective cursor; the generated runner's
/// idempotency filter reads the row's own recorded position. Nothing requires the two to agree,
/// and a stream that has no cursor row gives the detector nothing to compare while leaving the
/// filter a row sitting at the head of the stream.</para>
/// <para>The invariant these tests pin does not depend on which way that is resolved: an event
/// the filter discards as already applied must have been answered by something. If detection
/// found no inversion and the filter discards the batch anyway, the events are gone, the row is
/// permanently wrong, and nothing is left pending to repair it.</para>
/// <para>The figures are from a measured incident: a row recording commit sequence 4847417 while
/// a batch of thirty-eight events between 4847325 and 4847363 had never been applied to it. The
/// batch is fed unsorted and carries a gap, because the recorded one did.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveIdempotencyFilter.cs</code-under-test>
public class NeverAppliedEventsAreNotDroppedTests {

  private const long ROW_MARK = 4847417;
  private const long BLOCK_FIRST = 4847325;
  private const long BLOCK_LAST = 4847363;
  private const long BLOCK_GAP = 4847361;

  private sealed class TestEvent : IEvent;

  private static Guid _uuidv7() => (Guid)TrackedGuid.New();

  private static MessageEnvelope<IEvent> _envelope(Guid messageId) => new() {
    MessageId = MessageId.From(messageId),
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    Hops = [],
    Payload = new TestEvent()
  };

  private static StreamEventData _raw(Guid streamId, Guid eventId, long? commitSequence) => new() {
    StreamId = streamId,
    EventId = eventId,
    EventType = "T",
    EventData = "{}",
    EventWorkId = (Guid)TrackedGuid.New(),
    CommitSequence = commitSequence,
  };

  /// <summary>
  /// The batch below the row's mark, as sequences. One value is missing from the run and the
  /// lowest arrives last, so a fix that only works on a sorted, contiguous batch does not pass.
  /// </summary>
  private static List<long> _batchSequences() {
    var sequences = new List<long>();
    for (var seq = BLOCK_FIRST + 1; seq <= BLOCK_LAST; seq++) {
      if (seq != BLOCK_GAP) {
        sequences.Add(seq);
      }
    }
    sequences.Add(BLOCK_FIRST);
    return sequences;
  }

  private static (List<MessageEnvelope<IEvent>> Events, ILookup<Guid, StreamEventData> Raw, Dictionary<Guid, long> Sequences)
      _batch(Guid streamId) {
    var events = new List<MessageEnvelope<IEvent>>();
    var rows = new List<StreamEventData>();
    var sequences = new Dictionary<Guid, long>();
    foreach (var seq in _batchSequences()) {
      var eventId = _uuidv7();
      events.Add(_envelope(eventId));
      rows.Add(_raw(streamId, eventId, seq));
      sequences[eventId] = seq;
    }
    return (events, rows.ToLookup(r => r.EventId), sequences);
  }

  [Test]
  public async Task NoCursorRow_RowAheadOfTheBatch_AnEventTheFilterDiscardsWasAnsweredByDetectionAsync() {
    var streamId = _uuidv7();
    var rowLastAppliedEventId = _uuidv7().ToString("D");
    var (events, raw, sequences) = _batch(streamId);

    // The stream has no cursor row, so both halves the detector reads are null. That is the
    // state the affected streams were in: a row recording a position, and no cursor beside it.
    var anchor = PerspectiveWorker._resolveInversionAnchor(
      events, raw, lastProcessedEventId: null, lastProcessedCommitSequence: null);

    var discarded = events.Count(e => PerspectiveIdempotencyFilter.IsAlreadyApplied(
      rowLastAppliedEventId, ROW_MARK, e.MessageId.Value, sequences[e.MessageId.Value]));

    // Detection has two halves (#1347): the worker compares against the cursor, and the runner, before it lets the
    // filter drop anything, compares against the row's own position. Together they anchor on the later of the two.
    var runnerAnchor = PerspectiveIdempotencyFilter.FindRewindAnchor(
      rowLastAppliedEventId, ROW_MARK, events.Select(e => (e.MessageId.Value, (long?)sequences[e.MessageId.Value])));

    await Assert.That(discarded == 0 || anchor is not null || runnerAnchor is not null)
      .IsTrue()
      .Because(
        "The filter discards every event below the row's mark. Unless detection anchors a rewind, " +
        "those events are deleted without ever being applied and nothing is left to repair the row.");
  }

}
