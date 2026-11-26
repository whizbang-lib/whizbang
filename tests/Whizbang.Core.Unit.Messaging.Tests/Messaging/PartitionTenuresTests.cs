// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The tenure ledger is how every write names the partition epoch its work was claimed under, with no worker having to
/// carry it (#2556): the coordinator records each claim's stream tenures and each fetched work id under its stream's
/// tenure, and reads them back when it writes a result. Bounded by two generations, so a process that claims for days
/// keeps at most two generations of entries.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
/// <code-under-test>src/Whizbang.Core/Messaging/PartitionTenures.cs</code-under-test>
public class PartitionTenuresTests {
  [Test]
  public async Task AClaimedStream_HasItsTenure_AndAWorkIdFetchedOnIt_HasTheSameAsync() {
    var tenures = new PartitionTenures();
    var stream = (Guid)TrackedGuid.New();
    var work = (Guid)TrackedGuid.New();

    tenures.RecordClaim(new Dictionary<Guid, long> { [stream] = 4 }, new Dictionary<Guid, int> { [stream] = 17 });
    tenures.RecordFetched(work, stream);

    await Assert.That(tenures.OfStream(stream)).IsEqualTo(new PartitionTenure(17, 4));
    await Assert.That(tenures.OfWork(work)).IsEqualTo(new PartitionTenure(17, 4));
  }

  [Test]
  public async Task AWorkIdFetchedBeforeAReclaim_KeepsTheTenureItWasFetchedUnderAsync() {
    var tenures = new PartitionTenures();
    var stream = (Guid)TrackedGuid.New();
    var work = (Guid)TrackedGuid.New();
    tenures.RecordClaim(new Dictionary<Guid, long> { [stream] = 4 }, new Dictionary<Guid, int> { [stream] = 17 });
    tenures.RecordFetched(work, stream);

    tenures.RecordClaim(new Dictionary<Guid, long> { [stream] = 6 }, new Dictionary<Guid, int> { [stream] = 17 });

    await Assert.That(tenures.OfWork(work)).IsEqualTo(new PartitionTenure(17, 4))
      .Because("work fetched under epoch 4 is written under epoch 4, so a move and a move back cannot make it current again");
    await Assert.That(tenures.OfStream(stream)).IsEqualTo(new PartitionTenure(17, 6));
  }

  [Test]
  public async Task UnknownWork_OrAStreamWithNoTenure_HasNoneAsync() {
    var tenures = new PartitionTenures();
    var stream = (Guid)TrackedGuid.New();
    var work = (Guid)TrackedGuid.New();

    tenures.RecordFetched(work, stream);
    tenures.RecordFetched((Guid)TrackedGuid.New(), null);

    await Assert.That(tenures.OfWork(work)).IsNull().Because("off map mode a claim carries no epoch, so nothing is fenced");
    await Assert.That(tenures.OfWork((Guid)TrackedGuid.New())).IsNull();
    await Assert.That(tenures.OfStream(stream)).IsNull();
  }

  [Test]
  public async Task ForgottenWork_HasNoTenureAsync() {
    var tenures = new PartitionTenures();
    var stream = (Guid)TrackedGuid.New();
    var work = (Guid)TrackedGuid.New();
    tenures.RecordClaim(new Dictionary<Guid, long> { [stream] = 4 }, new Dictionary<Guid, int> { [stream] = 17 });
    tenures.RecordFetched(work, stream);

    tenures.Forget([work]);

    await Assert.That(tenures.OfWork(work)).IsNull();
  }

  [Test]
  public async Task ForgottenPartitions_LoseTheirStreamsAndWork_ThroughTheirRemainder_AndOthersKeepTheirsAsync() {
    var tenures = new PartitionTenures();
    var moved = (Guid)TrackedGuid.New();
    var kept = (Guid)TrackedGuid.New();
    var work = (Guid)TrackedGuid.New();
    tenures.RecordClaim(new Dictionary<Guid, long> { [moved] = 4, [kept] = 2 }, new Dictionary<Guid, int> { [moved] = 10_017, [kept] = 18 });
    tenures.RecordFetched(work, moved);

    tenures.ForgetPartitions([17]);
    tenures.ForgetPartitions([]);

    await Assert.That(tenures.OfStream(moved).HasValue).IsFalse().Because("a row's partition 10,017 is the table's partition 17");
    await Assert.That(tenures.OfWork(work).HasValue).IsFalse();
    await Assert.That(tenures.OfStream(kept).HasValue).IsTrue();
  }

  [Test]
  public async Task PastItsCapacity_TheLedgerKeepsTwoGenerations_AndDropsTheOldestAsync() {
    var tenures = new PartitionTenures(capacity: 2);
    var streams = Enumerable.Range(0, 5).Select(_ => (Guid)TrackedGuid.New()).ToArray();

    foreach (var stream in streams) {
      tenures.RecordClaim(new Dictionary<Guid, long> { [stream] = 1 }, new Dictionary<Guid, int> { [stream] = 1 });
    }

    await Assert.That(tenures.OfStream(streams[0])).IsNull().Because("the first generation was dropped");
    await Assert.That(tenures.OfStream(streams[4]).HasValue).IsTrue();
    await Assert.That(tenures.OfStream(streams[3]).HasValue).IsTrue().Because("the previous generation is still read");
  }

  // --- the perspective write's tenure (#2557): the stream's perspective work in flight, never its last claim ----------

  [Test]
  public async Task AStreamsWriteTenure_IsItsPerspectiveWorkInFlight_AndEndsWithTheLastOfItAsync() {
    var tenures = new PartitionTenures();
    var stream = (Guid)TrackedGuid.New();
    var first = (Guid)TrackedGuid.New();
    var second = (Guid)TrackedGuid.New();
    _claim(tenures, stream, partition: 17, epoch: 4);
    tenures.RecordFetchedPerspective(first, stream);
    tenures.RecordFetchedPerspective(second, stream);

    var whileBoth = tenures.OfPerspectiveWrite(stream);
    tenures.Forget([first]);
    var whileOne = tenures.OfPerspectiveWrite(stream);
    tenures.Forget([second]);

    await Assert.That(whileBoth).IsEqualTo(new PartitionTenure(17, 4));
    await Assert.That(whileOne).IsEqualTo(new PartitionTenure(17, 4));
    await Assert.That(tenures.OfPerspectiveWrite(stream).HasValue).IsFalse()
      .Because("with nothing in flight, a later write of the stream (a local rebuild) applies no claimed work to fence");
    await Assert.That(tenures.OfStream(stream)).IsEqualTo(new PartitionTenure(17, 4)).Because("the claim's own entry is kept");
    await Assert.That(tenures.OfWork(first).HasValue).IsFalse();
  }

  [Test]
  public async Task AClaimedStream_WithNoPerspectiveWorkFetched_HasNoWriteTenureAsync() {
    var tenures = new PartitionTenures();
    var stream = (Guid)TrackedGuid.New();
    _claim(tenures, stream, partition: 17, epoch: 4);
    tenures.RecordFetched((Guid)TrackedGuid.New(), stream);

    await Assert.That(tenures.OfPerspectiveWrite(stream).HasValue).IsFalse()
      .Because("a partition moved by deadline leaves its claim behind, and that alone must never fence a rebuild");
  }

  [Test]
  public async Task NewerWork_WhileOlderIsInFlight_KeepsTheOlderEpochUntilItEndsAsync() {
    var tenures = new PartitionTenures();
    var stream = (Guid)TrackedGuid.New();
    var older = (Guid)TrackedGuid.New();
    var newer = (Guid)TrackedGuid.New();
    _claim(tenures, stream, partition: 17, epoch: 4);
    tenures.RecordFetchedPerspective(older, stream);
    _claim(tenures, stream, partition: 17, epoch: 6);
    tenures.RecordFetchedPerspective(newer, stream);

    var whileOlder = tenures.OfPerspectiveWrite(stream);
    tenures.Forget([older]);

    await Assert.That(whileOlder).IsEqualTo(new PartitionTenure(17, 4))
      .Because("while work claimed under the older epoch is in flight, the stream's writes are judged by it");
    await Assert.That(tenures.OfPerspectiveWrite(stream)).IsEqualTo(new PartitionTenure(17, 6));
  }

  [Test]
  public async Task PerspectiveWorkOnAStreamWithNoTenure_IsNotCounted_AndForgettingUnknownWorkChangesNothingAsync() {
    var tenures = new PartitionTenures();
    var stream = (Guid)TrackedGuid.New();
    var counted = (Guid)TrackedGuid.New();
    tenures.RecordFetchedPerspective((Guid)TrackedGuid.New(), stream);
    tenures.RecordFetchedPerspective((Guid)TrackedGuid.New(), null);
    _claim(tenures, stream, partition: 17, epoch: 4);
    tenures.RecordFetchedPerspective(counted, stream);

    tenures.Forget([(Guid)TrackedGuid.New()]);

    await Assert.That(tenures.OfPerspectiveWrite(stream)).IsEqualTo(new PartitionTenure(17, 4));
  }

  [Test]
  public async Task PerspectiveWorkDroppedPastCapacity_NoLongerHoldsItsStreamsWriteTenureAsync() {
    var tenures = new PartitionTenures(capacity: 1);
    var streams = Enumerable.Range(0, 3).Select(_ => (Guid)TrackedGuid.New()).ToArray();
    var works = streams.Select(_ => (Guid)TrackedGuid.New()).ToArray();
    for (var i = 0; i < streams.Length; i++) {
      _claim(tenures, streams[i], partition: 17, epoch: 4);
      tenures.RecordFetchedPerspective(works[i], streams[i]);
    }
    tenures.RecordFetchedPerspective(works[1], streams[1]);
    tenures.RecordFetchedPerspective(works[2], streams[2]);
    var previousStillHeld = tenures.OfPerspectiveWrite(streams[1]).HasValue;
    tenures.Forget([works[1]]);

    await Assert.That(tenures.OfPerspectiveWrite(streams[0]).HasValue).IsFalse()
      .Because("work dropped with its generation is no longer in flight, so it cannot hold a fence open");
    await Assert.That(previousStillHeld).IsTrue().Because("the previous generation's work is still in flight");
    await Assert.That(tenures.OfPerspectiveWrite(streams[1]).HasValue).IsFalse()
      .Because("work fetched again is counted once, so forgetting it once ends it");
    await Assert.That(tenures.OfPerspectiveWrite(streams[2]).HasValue).IsTrue();
  }

  [Test]
  public async Task ForgottenPartitions_EndTheirStreamsWriteTenuresAsync() {
    var tenures = new PartitionTenures();
    var moved = (Guid)TrackedGuid.New();
    var kept = (Guid)TrackedGuid.New();
    var movedWork = (Guid)TrackedGuid.New();
    _claim(tenures, moved, partition: 10_017, epoch: 4);
    _claim(tenures, kept, partition: 18, epoch: 2);
    tenures.RecordFetchedPerspective(movedWork, moved);
    tenures.RecordFetchedPerspective((Guid)TrackedGuid.New(), kept);

    tenures.ForgetPartitions([17]);
    tenures.Forget([movedWork]);

    await Assert.That(tenures.OfPerspectiveWrite(moved).HasValue).IsFalse();
    await Assert.That(tenures.OfPerspectiveWrite(kept).HasValue).IsTrue();
  }

  [Test]
  public async Task TheDefaults_RegisterOneLedgerForTheProcessAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetService<PartitionTenures>()).IsNotNull()
      .Because("every coordinator reads the same ledger, so no write can miss the tenure its claim recorded");
    await Assert.That(provider.GetService<PartitionTenures>()).IsSameReferenceAs(provider.GetService<PartitionTenures>());
  }

  [Test]
  public async Task Capacity_MustBePositiveAsync() =>
    await Assert.That(() => new PartitionTenures(capacity: 0)).Throws<ArgumentOutOfRangeException>();

  private static void _claim(PartitionTenures tenures, Guid stream, int partition, long epoch) =>
    tenures.RecordClaim(new Dictionary<Guid, long> { [stream] = epoch }, new Dictionary<Guid, int> { [stream] = partition });
}
