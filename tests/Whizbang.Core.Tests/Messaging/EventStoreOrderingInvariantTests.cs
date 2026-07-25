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
/// In-memory layer regression tests for a production cursor-inversion symptom.
/// Even if the producer (TrackedGuid) generates monotonic IDs, the event store must preserve
/// that order on append + retrieval. These tests assert the in-memory <c>InMemoryEventStore</c>
/// holds the ordering invariant for both single-threaded sequential appends and concurrent
/// appends to the same stream.
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
  /// 1,000 sequential appends to one stream, then GetStreamEvents must return them in
  /// monotonic event_id (UUIDv7) order. Locks the basic ordering invariant.
  /// </summary>
  [Test]
  public async Task SequentialAppends_ReturnedInMonotonicEventIdOrderAsync() {
    var store = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    var appendedIds = new List<Guid>();

    for (var i = 0; i < 1_000; i++) {
      var id = (Guid)TrackedGuid.NewMedo();
      appendedIds.Add(id);
      await store.AppendAsync(streamId, _envelope(id, new TestEvent(i)));
    }

    var retrievedList = new List<MessageEnvelope<TestEvent>>();
    await foreach (var env in store.ReadAsync<TestEvent>(streamId, fromSequence: 0, cancellationToken: CancellationToken.None)) {
      retrievedList.Add(env);
    }
    var retrievedIds = retrievedList.Select(e => e.MessageId.Value).ToArray();

    var inversions = 0;
    for (var i = 1; i < retrievedIds.Length; i++) {
      if (string.Compare(retrievedIds[i].ToString("D"), retrievedIds[i - 1].ToString("D"), StringComparison.Ordinal) <= 0) {
        inversions++;
      }
    }

    await Assert.That(retrievedIds.Length).IsEqualTo(appendedIds.Count);
    await Assert.That(inversions).IsEqualTo(0)
      .Because("InMemoryEventStore.GetStreamEventsAsync must return events in monotonic event_id order for sequential appends.");
  }

  /// <summary>
  /// Concurrent-append regression: 16 tasks each append 200 events to the SAME stream
  /// concurrently. After all writes complete, GetStreamEvents must return events sorted
  /// such that earlier UUIDv7 IDs come first. RED here = either UUIDv7 isn't monotonic
  /// (caught by TrackedGuidMonotonicityTests) OR the store doesn't preserve insertion
  /// order — both produce the same symptoms observed in production.
  /// </summary>
  [Test]
  public async Task ConcurrentAppendsToSameStream_RetrievedInMonotonicEventIdOrderAsync() {
    var store = new InMemoryEventStore();
    var streamId = Guid.NewGuid();
    const int taskCount = 16;
    const int perTask = 200;

    var allTasks = new Task[taskCount];
    for (var t = 0; t < taskCount; t++) {
      var taskIdx = t;
      allTasks[taskIdx] = Task.Run(async () => {
        for (var i = 0; i < perTask; i++) {
          var id = (Guid)TrackedGuid.NewMedo();
          await store.AppendAsync(streamId, _envelope(id, new TestEvent(taskIdx * perTask + i)));
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

    var inversions = 0;
    for (var i = 1; i < retrievedIds.Length; i++) {
      if (string.Compare(retrievedIds[i].ToString("D"), retrievedIds[i - 1].ToString("D"), StringComparison.Ordinal) < 0) {
        inversions++;
      }
    }

    await Assert.That(inversions).IsEqualTo(0)
      .Because($"After concurrent appends, GetStreamEvents must return events in monotonic event_id order. Found {inversions} inversions across {retrievedIds.Length} events. RED here = the same ordering bug observed in production at the in-memory event store layer.");
  }

  /// <summary>
  /// Producer-supplied IDs that are already non-monotonic (simulating the production bug):
  /// the store should still return events in event_id-sorted order, NOT insertion order. Locks
  /// the contract that the store is responsible for preserving event_id ordering on retrieval
  /// regardless of append timing.
  /// </summary>
  [Test]
  public async Task PreSortedRetrieval_WhenInsertedOutOfOrder_RetrievesInEventIdOrderAsync() {
    var store = new InMemoryEventStore();
    var streamId = Guid.NewGuid();

    // Build 5 IDs but append them in reverse order
    var ids = Enumerable.Range(0, 5).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();
    for (var i = ids.Length - 1; i >= 0; i--) {
      await store.AppendAsync(streamId, _envelope(ids[i], new TestEvent(i)));
    }

    var retrievedList = new List<MessageEnvelope<TestEvent>>();
    await foreach (var env in store.ReadAsync<TestEvent>(streamId, fromSequence: 0, cancellationToken: CancellationToken.None)) {
      retrievedList.Add(env);
    }
    var retrievedIds = retrievedList.Select(e => e.MessageId.Value).ToArray();

    // Expect ascending event_id order, NOT insertion order.
    var sortedExpected = ids.OrderBy(g => g.ToString("D"), StringComparer.Ordinal).ToArray();
    await Assert.That(retrievedIds.SequenceEqual(sortedExpected)).IsTrue()
      .Because("GetStreamEvents must return events sorted by event_id, not insertion order. This is the contract that downstream cursor-comparison code relies on.");
  }
}
