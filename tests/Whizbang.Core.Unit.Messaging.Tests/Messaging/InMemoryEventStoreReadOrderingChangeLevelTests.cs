// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// Change-level tests for the read order of
/// <see cref="InMemoryEventStore"/>'s <c>Read(fromSequence)</c> path: a stream is returned in stream order, the
/// version each event was appended at, as the Postgres stores read it, never by event id (#2551). These tests target
/// ONLY the read-ordering behavior, not the
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
  /// Insert events with hand-crafted IDs in REVERSE lex order. ReadAsync returns them in
  /// the order they were appended (their versions), never sorted by id (#2551).
  /// </summary>
  [Test]
  public async Task ReadAsync_FromSequenceZero_ReturnsEventsInStreamOrder_NotEventIdOrderAsync() {
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

    await Assert.That(read.SequenceEqual(sortedIds.Reverse())).IsTrue()
      .Because("#2551: ReadAsync(fromSequence: 0) returns a stream in stream order (the version each event was appended at), "
        + "as the Postgres stores read it; an id is time-ordered and never a stream position.");
  }

  /// <summary>
  /// fromSequence filtering on Version, and the slice returned in version order (#2551), never by EventId.
  /// </summary>
  [Test]
  public async Task ReadAsync_FromSequenceFilter_FiltersByVersion_AndReturnsStreamOrderAsync() {
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

    // Read from sequence 2: should include insertions 1, 2, 3 (skipping insertion 0, at version 1: #1913).
    // #2551: the result is in stream order, by version, never by EventId.
    var read = new List<Guid>();
    await foreach (var env in store.ReadAsync<TestEvent>(streamId, fromSequence: 2)) {
      read.Add(env.MessageId.Value);
    }

    var expected = new[] {
      Guid.Parse("019df109-0005-7000-8000-000000000000"),  // insertion 1
      Guid.Parse("019df109-0020-7000-8000-000000000000"),  // insertion 2
      Guid.Parse("019df109-0001-7000-8000-000000000000")   // insertion 3
    };
    await Assert.That(read.SequenceEqual(expected)).IsTrue()
      .Because("fromSequence filter applies on Version, and the returned slice is in version order, never by EventId.");
    await Assert.That(read.Count).IsEqualTo(3)
      .Because("Insertion 0 (Version=1) is filtered out by fromSequence=2.");
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
    await Assert.That(ids.SequenceEqual([id])).IsTrue();
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

  /// <summary>
  /// #2551: a read after an event the stream holds starts after that event's version and returns the stream in version
  /// order, never by id. The ids run against the appends, so an id range would return a different set.
  /// </summary>
  [Test]
  public async Task ReadAsync_AfterAHeldEvent_ReturnsTheLaterVersions_InStreamOrderAsync() {
    var store = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    var ids = new[] {
      Guid.Parse("019df109-0030-7000-8000-000000000000"),
      Guid.Parse("019df109-0020-7000-8000-000000000000"),
      Guid.Parse("019df109-0010-7000-8000-000000000000"),
    };
    for (var i = 0; i < ids.Length; i++) {
      await store.AppendAsync(streamId, _envelope(ids[i], i));
    }

    var read = new List<Guid>();
    await foreach (var env in store.ReadAsync<TestEvent>(streamId, fromEventId: ids[0])) {
      read.Add(env.MessageId.Value);
    }
    var whole = new List<Guid>();
    await foreach (var env in store.ReadAsync<TestEvent>(streamId, fromEventId: null)) {
      whole.Add(env.MessageId.Value);
    }

    await Assert.That(read).IsEquivalentTo([ids[1], ids[2]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the events after the first one appended, in the order appended, though their ids sort below it");
    await Assert.That(whole).IsEquivalentTo(ids, TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  /// <summary>
  /// An anchor the stream does not hold has no position in it, so the read compares it by id, as the Postgres stores
  /// read it (#2379), and still returns what it finds in stream order.
  /// </summary>
  [Test]
  public async Task ReadAsync_AfterAnEventTheStreamDoesNotHold_ComparesByIdAndReturnsStreamOrderAsync() {
    var store = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    var ids = new[] {
      Guid.Parse("019df109-0030-7000-8000-000000000000"),
      Guid.Parse("019df109-0010-7000-8000-000000000000"),
      Guid.Parse("019df109-0020-7000-8000-000000000000"),
    };
    for (var i = 0; i < ids.Length; i++) {
      await store.AppendAsync(streamId, _envelope(ids[i], i));
    }

    var read = new List<Guid>();
    await foreach (var env in store.ReadAsync<TestEvent>(streamId, fromEventId: Guid.Parse("019df109-0015-7000-8000-000000000000"))) {
      read.Add(env.MessageId.Value);
    }

    await Assert.That(read).IsEquivalentTo([ids[0], ids[2]], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }
}
