// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
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
/// The in-memory event store returns a stream in stream order, the order its events were appended in (their versions),
/// as the Postgres stores read it, never sorted by id (#2551). An id is time-ordered and never a stream position. These
/// lock that for sequential appends, concurrent appends (each producer's events stay in the order it appended them), and
/// appends whose ids run backward.
/// </summary>
public class EventStoreOrderingInvariantTests {

  private sealed record TestEvent(int Index) : IEvent;

  private static MessageEnvelope<TestEvent> _envelope(Guid eventId, TestEvent payload) =>
    new() {
      MessageId = MessageId.From(eventId),
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = []
    };

  /// <summary>
  /// 1,000 sequential appends to one stream are returned in the order they were appended.
  /// </summary>
  [Test]
  public async Task SequentialAppends_ReturnedInAppendOrderAsync() {
    var store = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    var appendedIds = new List<Guid>();

    for (var i = 0; i < 1_000; i++) {
      var id = (Guid)TrackedGuid.New();
      appendedIds.Add(id);
      await store.AppendAsync(streamId, _envelope(id, new TestEvent(i)));
    }

    var retrievedList = new List<MessageEnvelope<TestEvent>>();
    await foreach (var env in store.ReadAsync<TestEvent>(streamId, fromSequence: 0, cancellationToken: CancellationToken.None)) {
      retrievedList.Add(env);
    }
    var retrievedIds = retrievedList.Select(e => e.MessageId.Value).ToArray();

    await Assert.That(retrievedIds.SequenceEqual(appendedIds)).IsTrue()
      .Because("a stream is read in the order it was appended, its versions");
  }

  /// <summary>
  /// 16 tasks each append 200 events to the SAME stream concurrently. Every event is read back once, and each producer's
  /// events come back in the order that producer appended them: the stream's order is its appends, never its ids.
  /// </summary>
  [Test]
  public async Task ConcurrentAppendsToSameStream_EachProducersEventsKeepTheirAppendOrderAsync() {
    var store = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    const int taskCount = 16;
    const int perTask = 200;

    var allTasks = new Task[taskCount];
    for (var t = 0; t < taskCount; t++) {
      var taskIdx = t;
      allTasks[taskIdx] = Task.Run(async () => {
        for (var i = 0; i < perTask; i++) {
          var id = (Guid)TrackedGuid.New();
          await store.AppendAsync(streamId, _envelope(id, new TestEvent((taskIdx * perTask) + i)));
        }
      });
    }
    await Task.WhenAll(allTasks);

    var retrievedList = new List<MessageEnvelope<TestEvent>>();
    await foreach (var env in store.ReadAsync<TestEvent>(streamId, fromSequence: 0, cancellationToken: CancellationToken.None)) {
      retrievedList.Add(env);
    }
    var retrievedIds = retrievedList.Select(e => e.MessageId.Value).ToArray();

    await Assert.That(retrievedIds.Length).IsEqualTo(taskCount * perTask);

    var indexes = retrievedList.Select(e => e.Payload.Index).ToArray();
    await Assert.That(indexes.Distinct().Count()).IsEqualTo(taskCount * perTask)
      .Because("every appended event is read back once");
    for (var t = 0; t < taskCount; t++) {
      var producer = t;
      var own = indexes.Where(i => i / perTask == producer).ToArray();
      await Assert.That(own.SequenceEqual(own.Order())).IsTrue()
        .Because($"producer {producer}'s events are read in the order it appended them");
    }
  }

  /// <summary>
  /// Events appended with ids that run backward are read in the order they were appended, never sorted by id (#2551).
  /// </summary>
  [Test]
  public async Task Retrieval_WhenIdsRunBackward_ReturnsTheAppendOrder_NotIdOrderAsync() {
    var store = new InMemoryEventStore();
    var streamId = Guid.NewGuid();

    // Build 5 IDs but append them in reverse order
    var ids = Enumerable.Range(0, 5).Select(_ => (Guid)TrackedGuid.New()).ToArray();
    for (var i = ids.Length - 1; i >= 0; i--) {
      await store.AppendAsync(streamId, _envelope(ids[i], new TestEvent(i)));
    }

    var retrievedList = new List<MessageEnvelope<TestEvent>>();
    await foreach (var env in store.ReadAsync<TestEvent>(streamId, fromSequence: 0, cancellationToken: CancellationToken.None)) {
      retrievedList.Add(env);
    }
    var retrievedIds = retrievedList.Select(e => e.MessageId.Value).ToArray();

    var appendOrder = ids.Reverse().ToArray();
    await Assert.That(retrievedIds.SequenceEqual(appendOrder)).IsTrue()
      .Because("a stream is read in the order it was appended, whatever its ids say");
  }
}
