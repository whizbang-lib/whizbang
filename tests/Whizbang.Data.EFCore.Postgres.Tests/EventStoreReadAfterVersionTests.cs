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

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The guard on the one order of a stream's reads (#2379): "the events after an event" are the events after its version,
/// never after its id. Every stream here is written so its first event carries its highest id, the shape whose rewind
/// replayed nothing from a snapshot at its first event, forever. An id comparison brought back into either read, the
/// per-stream read or the batched read of a set-based batch, fails these.
/// </summary>
/// <docs>fundamentals/perspectives/rewind#one-order-for-a-stream</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class EventStoreReadAfterVersionTests : EFCoreTestBase {
  private static readonly Type[] _types = [typeof(OrderCreatedEvent)];

  // Each stream: version 1 with the highest id, version 2 with the lowest, version 3 between them.
  private static readonly DateTimeOffset _minted = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task ReadPolymorphicAsync_AfterTheFirstEvent_WhoseIdIsTheHighest_ReadsVersionsTwoAndThreeAsync() {
    await using var context = CreateDbContext();
    IEventStore store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var stream = (Guid)TrackedGuid.New();
    var ids = await _appendHighestIdFirstAsync(store, stream, offsetSeconds: 0);

    var read = await _readAsync(store, stream, ids[0]);

    await Assert.That(read).IsEquivalentTo([ids[1], ids[2]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the events after version 1 are versions 2 and 3, whatever their ids");
  }

  [Test]
  public async Task ReadPolymorphicAsync_AfterAnEventTheStreamDoesNotHold_ReadsAfterItsIdAsync() {
    await using var context = CreateDbContext();
    IEventStore store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var stream = (Guid)TrackedGuid.New();
    var ids = await _appendHighestIdFirstAsync(store, stream, offsetSeconds: 0);
    // An id between version 2's and version 3's, held by no stream.
    var unheld = Guid.CreateVersion7(_minted.AddMilliseconds(500));

    var read = await _readAsync(store, stream, unheld);

    await Assert.That(read).IsEquivalentTo([ids[0], ids[2]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("an anchor with no position in the stream keeps the id order the read always had for one");
  }

  [Test]
  public async Task ReadPolymorphicManyAsync_CutsEachStreamAtItsAnchorsVersion_FromTheEarliestByVersionAsync() {
    await using var context = CreateDbContext();
    IEventStore store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var (one, two) = ((Guid)TrackedGuid.New(), (Guid)TrackedGuid.New());
    var oneIds = await _appendHighestIdFirstAsync(store, one, offsetSeconds: 0);
    var twoIds = await _appendHighestIdFirstAsync(store, two, offsetSeconds: 10);
    // A type the batch does not ask for, above every anchor: read past, never returned.
    await store.AppendAsync(two, new MessageEnvelope<OrderShippedEvent> {
      MessageId = MessageId.From(Guid.CreateVersion7(_minted.AddSeconds(30))),
      Payload = new OrderShippedEvent { OrderId = two, TrackingNumber = "t" },
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    });

    // Stream one asked after version 2 and after version 1: the earliest by version is version 1, whose id is the highest.
    var read = await store.ReadPolymorphicManyAsync([(one, oneIds[1]), (one, oneIds[0]), (two, twoIds[1])], _types);

    await Assert.That(read).IsNotNull();
    await Assert.That(read![one].Select(e => e.MessageId.Value)).IsEquivalentTo([oneIds[1], oneIds[2]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("stream one is read after version 1, its earliest anchor in stream order");
    await Assert.That(read[two].Select(e => e.MessageId.Value)).IsEquivalentTo([twoIds[2]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("stream two is read after version 2 only, though stream one's floor reaches below it");
  }

  [Test]
  public async Task ReadPolymorphicManyAsync_AnAnchorItsStreamDoesNotHold_LeavesEachPairToItsOwnReadAsync() {
    await using var context = CreateDbContext();
    IEventStore store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var (one, two) = ((Guid)TrackedGuid.New(), (Guid)TrackedGuid.New());
    var oneIds = await _appendHighestIdFirstAsync(store, one, offsetSeconds: 0);
    var twoIds = await _appendHighestIdFirstAsync(store, two, offsetSeconds: 10);

    var unheld = await store.ReadPolymorphicManyAsync([(one, Guid.CreateVersion7(_minted.AddMilliseconds(500)))], _types);
    var anothers = await store.ReadPolymorphicManyAsync([(one, oneIds[0]), (two, oneIds[1])], _types);
    var fromStart = await store.ReadPolymorphicManyAsync([(two, null)], _types);

    await Assert.That(unheld).IsNull().Because("an anchor no stream holds has no position the batch can cut at");
    await Assert.That(anothers).IsNull().Because("an anchor another stream holds has no position in this one");
    await Assert.That(fromStart![two].Select(e => e.MessageId.Value)).IsEquivalentTo(twoIds, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a batch with no anchor reads each stream from its start");
  }

  // ── The between-checkpoint reads (#2381): the events lifecycle receptors are handed for a perspective's batch ──────

  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_AfterTheFirstEvent_WhoseIdIsTheHighest_ReadsThroughTheUpperVersionAsync() {
    await using var context = CreateDbContext();
    IEventStore store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var stream = (Guid)TrackedGuid.New();
    var ids = await _appendHighestIdFirstAsync(store, stream, offsetSeconds: 0);

    var read = await store.GetEventsBetweenPolymorphicAsync(stream, ids[0], ids[2], _types);

    await Assert.That(read.Select(e => e.MessageId.Value)).IsEquivalentTo([ids[1], ids[2]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the batch after version 1 through version 3 is versions 2 and 3, whatever their ids");
  }

  [Test]
  public async Task GetEventsBetweenAsync_FromTheStartThroughVersionTwo_ReadsVersionsOneAndTwoInStreamOrderAsync() {
    await using var context = CreateDbContext();
    IEventStore store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var stream = (Guid)TrackedGuid.New();
    var ids = await _appendHighestIdFirstAsync(store, stream, offsetSeconds: 0);

    var read = await store.GetEventsBetweenAsync<OrderCreatedEvent>(stream, null, ids[1]);

    await Assert.That(read.Select(e => e.MessageId.Value)).IsEquivalentTo([ids[0], ids[1]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the events through version 2 are versions 1 and 2, not the events whose ids sort at or below it");
  }

  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_BetweenEventsTheStreamDoesNotHold_SelectsByTheirIdsAsync() {
    await using var context = CreateDbContext();
    IEventStore store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var stream = (Guid)TrackedGuid.New();
    var ids = await _appendHighestIdFirstAsync(store, stream, offsetSeconds: 0);
    // Ids between version 2's and version 3's, and between version 3's and version 1's, held by no stream.
    var unheldLow = Guid.CreateVersion7(_minted.AddMilliseconds(500));
    var unheldHigh = Guid.CreateVersion7(_minted.AddSeconds(3));

    var after = await store.GetEventsBetweenPolymorphicAsync(stream, unheldLow, Guid.Empty, _types);
    var through = await store.GetEventsBetweenPolymorphicAsync(stream, null, unheldHigh, _types);

    await Assert.That(after.Select(e => e.MessageId.Value)).IsEquivalentTo([ids[0], ids[2]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a lower checkpoint with no position in the stream keeps the id selection, read in stream order");
    await Assert.That(through.Select(e => e.MessageId.Value)).IsEquivalentTo([ids[1], ids[2]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("an upper checkpoint with no position in the stream keeps the id selection, read in stream order");
  }

  /// <summary>Appends three events: version 1 with the highest id, version 2 the lowest, version 3 between.</summary>
  private static async Task<Guid[]> _appendHighestIdFirstAsync(IEventStore store, Guid stream, int offsetSeconds) {
    var minted = _minted.AddSeconds(offsetSeconds);
    Guid[] ids = [
      Guid.CreateVersion7(minted.AddSeconds(5)),
      Guid.CreateVersion7(minted),
      Guid.CreateVersion7(minted.AddSeconds(1)),
    ];
    foreach (var id in ids) {
      await store.AppendAsync(stream, new MessageEnvelope<OrderCreatedEvent> {
        MessageId = MessageId.From(id),
        Payload = new OrderCreatedEvent { OrderId = stream, CustomerName = "c" },
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = [],
      });
    }
    return ids;
  }

  private static async Task<List<Guid>> _readAsync(IEventStore store, Guid stream, Guid? from) {
    var read = new List<Guid>();
    await foreach (var envelope in store.ReadPolymorphicAsync(stream, from, _types)) {
      read.Add(envelope.MessageId.Value);
    }
    return read;
  }
}
