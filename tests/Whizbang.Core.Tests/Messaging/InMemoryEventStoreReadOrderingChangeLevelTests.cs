using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Change-level tests for the OrderBy(EventId) change in
/// <see cref="InMemoryEventStore"/>'s <c>Read(fromSequence)</c> path. The fix
/// matches SQL semantics — events are returned in event_id (UUIDv7) order, not
/// insertion order. These tests target ONLY the read-ordering behavior, not the
/// broader event-store ordering invariants in
/// <see cref="EventStoreOrderingInvariantTests"/>.
/// </summary>
public class InMemoryEventStoreReadOrderingChangeLevelTests {

  private sealed record TestEvent(int Tag) : IEvent;

  private static MessageEnvelope<TestEvent> _envelope(Guid id, int tag) => new() {
    MessageId = MessageId.From(id),
    Payload = new TestEvent(tag),
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    Hops = []
  };

  /// <summary>
  /// Insert events with hand-crafted IDs in REVERSE lex order. ReadAsync must return
  /// them in event_id-sorted order (LEX), NOT in insertion order. RED here = the OrderBy
  /// fix is missing.
  /// </summary>
  [Test]
  public async Task ReadAsync_FromSequenceZero_ReturnsEventsInEventIdOrder_NotInsertionOrderAsync() {
    var store = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    var sortedIds = new[] {
      Guid.Parse("019df109-0001-7000-8000-000000000000"),
      Guid.Parse("019df109-0002-7000-8000-000000000000"),
      Guid.Parse("019df109-0003-7000-8000-000000000000"),
      Guid.Parse("019df109-0004-7000-8000-000000000000"),
      Guid.Parse("019df109-0005-7000-8000-000000000000")
    };

    // Insert in REVERSE lex order
    for (var i = sortedIds.Length - 1; i >= 0; i--) {
      await store.AppendAsync(streamId, _envelope(sortedIds[i], i));
    }

    var read = new List<Guid>();
    await foreach (var env in store.ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      read.Add(env.MessageId.Value);
    }

    await Assert.That(read.SequenceEqual(sortedIds)).IsTrue()
      .Because("ReadAsync(fromSequence: 0) must return events in event_id-sorted order, regardless of insertion order. This matches the SQL ORDER BY event_id ASC contract.");
  }

  /// <summary>
  /// fromSequence filtering still works after the OrderBy(EventId) change. Each
  /// EventRecord has both Version (insertion sequence) and EventId. The filter applies
  /// on Version; the sort applies on EventId.
  /// </summary>
  [Test]
  public async Task ReadAsync_FromSequenceFilter_StillFiltersByInsertionVersion_ButReturnsSortedAsync() {
    var store = new InMemoryEventStore();
    var streamId = Guid.NewGuid();

    var ids = new[] {
      Guid.Parse("019df109-0010-7000-8000-000000000000"),  // insertion 0
      Guid.Parse("019df109-0005-7000-8000-000000000000"),  // insertion 1, lex SMALLER than first
      Guid.Parse("019df109-0020-7000-8000-000000000000"),  // insertion 2
      Guid.Parse("019df109-0001-7000-8000-000000000000")   // insertion 3, lex SMALLEST
    };
    for (var i = 0; i < ids.Length; i++) {
      await store.AppendAsync(streamId, _envelope(ids[i], i));
    }

    // Read from sequence 1 — should include insertions 1, 2, 3 (skipping insertion 0).
    // Result is sorted by EventId.
    var read = new List<Guid>();
    await foreach (var env in store.ReadAsync<TestEvent>(streamId, fromSequence: 1)) {
      read.Add(env.MessageId.Value);
    }

    var expected = new[] {
      Guid.Parse("019df109-0001-7000-8000-000000000000"),  // insertion 3
      Guid.Parse("019df109-0005-7000-8000-000000000000"),  // insertion 1
      Guid.Parse("019df109-0020-7000-8000-000000000000")   // insertion 2
    };
    await Assert.That(read.SequenceEqual(expected)).IsTrue()
      .Because("fromSequence filter applies on Version (insertion order); the returned slice is then sorted by EventId.");
    await Assert.That(read.Count).IsEqualTo(3)
      .Because("Insertion 0 (Version=0) is filtered out by fromSequence=1.");
  }

  /// <summary>
  /// Empty stream sanity: ReadAsync on a non-existent stream yields nothing, regardless of
  /// the OrderBy change.
  /// </summary>
  [Test]
  public async Task ReadAsync_EmptyStream_YieldsNoEventsAsync() {
    var store = new InMemoryEventStore();
    var streamId = Guid.NewGuid();

    var count = 0;
    await foreach (var _ in store.ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      count++;
    }
    await Assert.That(count).IsEqualTo(0);
  }

  /// <summary>
  /// Single-event stream: trivial read, just makes sure the OrderBy doesn't break the
  /// degenerate case.
  /// </summary>
  [Test]
  public async Task ReadAsync_SingleEvent_YieldsThatEventAsync() {
    var store = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    var id = Guid.Parse("019df109-aaaa-7000-8000-000000000000");
    await store.AppendAsync(streamId, _envelope(id, 42));

    var ids = new List<Guid>();
    await foreach (var env in store.ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      ids.Add(env.MessageId.Value);
    }
    await Assert.That(ids.SequenceEqual(new[] { id })).IsTrue();
  }

  /// <summary>
  /// Backward-compatibility check: Read(fromSequence) returning event_id-sorted matches
  /// the existing ReadByEventId (fromEventId: null) path. Both should produce the same
  /// result for the same stream contents.
  /// </summary>
  [Test]
  public async Task ReadAsync_FromSequence_MatchesReadByEventIdAsync() {
    var store = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    var ids = new[] {
      Guid.Parse("019df109-0050-7000-8000-000000000000"),
      Guid.Parse("019df109-0010-7000-8000-000000000000"),
      Guid.Parse("019df109-0030-7000-8000-000000000000"),
      Guid.Parse("019df109-0020-7000-8000-000000000000"),
      Guid.Parse("019df109-0040-7000-8000-000000000000")
    };
    for (var i = 0; i < ids.Length; i++) {
      await store.AppendAsync(streamId, _envelope(ids[i], i));
    }

    var fromSeq = new List<Guid>();
    await foreach (var env in store.ReadAsync<TestEvent>(streamId, fromSequence: 0)) {
      fromSeq.Add(env.MessageId.Value);
    }
    var fromEvent = new List<Guid>();
    await foreach (var env in store.ReadAsync<TestEvent>(streamId, fromEventId: null)) {
      fromEvent.Add(env.MessageId.Value);
    }

    await Assert.That(fromSeq.SequenceEqual(fromEvent)).IsTrue()
      .Because("ReadAsync(fromSequence: 0) and ReadAsync(fromEventId: null) must yield identical event_id-sorted output now that both paths sort by EventId.");
  }
}
