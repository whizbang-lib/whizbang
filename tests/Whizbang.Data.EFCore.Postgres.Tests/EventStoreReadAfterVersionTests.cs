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
