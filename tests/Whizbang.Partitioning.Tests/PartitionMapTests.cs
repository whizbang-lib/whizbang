// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Partitioning.Tests;

/// <summary>
/// The explicit partition-to-member map the assigner publishes (#2558): a join or leave moves about 1/N of the
/// partitions, evenly, and nothing else moves. Pure: no clock, no I/O. The property tests walk seeded random histories,
/// so every run walks the same ones.
/// </summary>
[Category("Workers")]
public class PartitionMapTests {
  private const int P = PartitionMap.PARTITION_COUNT;
  private static readonly Guid _a = new("00000000-0000-0000-0000-00000000000a");
  private static readonly Guid _b = new("00000000-0000-0000-0000-00000000000b");
  private static readonly Guid _c = new("00000000-0000-0000-0000-00000000000c");

  private static PartitionAssignment _published(IReadOnlyList<Guid> members, IReadOnlyList<short>? owners) =>
    new(Epoch: 4, Revision: 2, AssignerInstanceId: _a, Members: members,
      PublishedAt: DateTimeOffset.UnixEpoch, LeaseExpiresAt: DateTimeOffset.UnixEpoch.AddMinutes(1), Owners: owners);

  private static int _ceil(int members) => (P + members - 1) / members;

  // --- the first map is the modulo the claims already apply ---------------------------------------

  [Test]
  public async Task Initial_IsTheModuloOverTheMembers_SoTheFirstMapMovesNothingAsync() {
    var map = PartitionMap.Initial([_a, _b, _c]);

    await Assert.That(map.Owners.Count).IsEqualTo(10_000)
      .Because("the owner fixed the partition count (2026-11-23); changing it re-partitions every stream");
    await Assert.That(Enumerable.Range(0, P).All(p => map.OwnerOf(p) == new[] { _a, _b, _c }[p % 3])).IsTrue()
      .Because("claimers rank by partition % count today, so the first map must agree with it partition for partition");
  }

  [Test]
  public async Task Initial_ListsAMemberOnce_WhateverTimesItIsGivenAsync() {
    var map = PartitionMap.Initial([_a, _b, _a]);

    await Assert.That(map.Members).IsEquivalentTo([_a, _b], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(map.CountOf(_a)).IsEqualTo(P / 2);
  }

  [Test]
  public async Task Initial_WithNoMembers_IsTheEmptyMapAsync() {
    var map = PartitionMap.Initial([]);

    await Assert.That(map.Members).IsEmpty();
    await Assert.That(map.Owners).IsEmpty();
    await Assert.That(map).IsSameReferenceAs(PartitionMap.Empty);
  }

  [Test]
  public async Task OwnerOf_ANegativePartition_IsRefusedAsync() {
    await Assert.That(() => PartitionMap.Initial([_a]).OwnerOf(-1)).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task OwnerOf_TheEmptyMap_IsRefusedAsync() {
    await Assert.That(() => PartitionMap.Empty.OwnerOf(0)).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task OwnerOf_APartitionPastTheCount_WrapsAsTheClaimDoesAsync() {
    // A claim worker configured with more partitions than the map holds still finds an owner for every partition: the
    // SQL predicate reads owners[(p % cardinality) + 1], so nothing goes unowned.
    var map = PartitionMap.Initial([_a, _b, _c]);

    await Assert.That(map.OwnerOf(P + 4)).IsEqualTo(map.OwnerOf(4));
  }

  [Test]
  public async Task CountOf_AnInstanceThatIsNotAMember_IsZeroAsync() {
    await Assert.That(PartitionMap.Initial([_a, _b]).CountOf(_c)).IsEqualTo(0);
  }

  // --- reading back what was published ------------------------------------------------------------

  [Test]
  public async Task FromPublished_Nothing_IsTheEmptyMapAsync() {
    await Assert.That(PartitionMap.FromPublished(null)).IsSameReferenceAs(PartitionMap.Empty);
  }

  [Test]
  public async Task FromPublished_TheOlderShape_IsTheModuloItsClaimersUseAsync() {
    // An assigner from an older release publishes members only; its claimers rank by modulo. A new assigner taking over
    // starts from exactly that, so its first publish moves nothing.
    var map = PartitionMap.FromPublished(_published([_b, _a], owners: null));

    await Assert.That(map.MovesTo(PartitionMap.Initial([_b, _a]))).IsEqualTo(0);
    await Assert.That(map.OwnerOf(0)).IsEqualTo(_b);
  }

  [Test]
  public async Task FromPublished_AMap_IsThatMapAsync() {
    var joined = PartitionMap.Initial([_a, _b]).Rebalance([_a, _b, _c]);

    var read = PartitionMap.FromPublished(_published(joined.Members, joined.Owners));

    await Assert.That(read.MovesTo(joined)).IsEqualTo(0);
    await Assert.That(read.Members).IsEquivalentTo(joined.Members, TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task FromPublished_AMapOfAnotherLength_IsReadAsTheOlderShapeAsync() {
    var map = PartitionMap.FromPublished(_published([_a, _b], owners: [1, 0, 1]));

    await Assert.That(map.MovesTo(PartitionMap.Initial([_a, _b]))).IsEqualTo(0);
  }

  // --- minimal moves --------------------------------------------------------------------------------

  [Test]
  public async Task Rebalance_TheSameMembers_MovesNothingAsync() {
    var map = PartitionMap.Initial([_a, _b, _c]).Rebalance([_a, _b]).Rebalance([_a, _b, _c]);

    await Assert.That(map.Rebalance([_a, _b, _c]).MovesTo(map)).IsEqualTo(0);
  }

  [Test]
  public async Task Rebalance_AJoin_TakesItsShareEvenlyFromEveryMember_AndMovesNothingElseAsync() {
    var before = PartitionMap.Initial([_a, _b]);

    var after = before.Rebalance([_a, _b, _c]);

    await Assert.That(after.CountOf(_c)).IsEqualTo(P / 3);
    await Assert.That(after.MovesTo(before)).IsEqualTo(P / 3)
      .Because("only the joiner's share moves; plain modulo would have moved two thirds of the partitions");
    await Assert.That(Math.Abs(before.CountOf(_a) - after.CountOf(_a) - (before.CountOf(_b) - after.CountOf(_b)))).IsLessThanOrEqualTo(1);
    await Assert.That(Enumerable.Range(0, P).Where(p => after.OwnerOf(p) != before.OwnerOf(p)).All(p => after.OwnerOf(p) == _c)).IsTrue();
  }

  [Test]
  public async Task Rebalance_ALeave_SpreadsTheLeaversPartitionsEvenly_AndMovesNothingElseAsync() {
    var before = PartitionMap.Initial([_a, _b, _c]);

    var after = before.Rebalance([_a, _c]);

    await Assert.That(after.MovesTo(before)).IsEqualTo(before.CountOf(_b));
    await Assert.That(Enumerable.Range(0, P).Where(p => after.OwnerOf(p) != before.OwnerOf(p)).All(p => before.OwnerOf(p) == _b)).IsTrue();
    await Assert.That(Math.Abs(after.CountOf(_a) - after.CountOf(_c))).IsLessThanOrEqualTo(1);
  }

  [Test]
  public async Task Rebalance_ToNoMembers_IsTheEmptyMapAsync() {
    await Assert.That(PartitionMap.Initial([_a]).Rebalance([])).IsSameReferenceAs(PartitionMap.Empty);
  }

  [Test]
  public async Task Rebalance_TheEmptyMap_IsTheInitialMapAsync() {
    await Assert.That(PartitionMap.Empty.Rebalance([_a, _b]).MovesTo(PartitionMap.Initial([_a, _b]))).IsEqualTo(0);
  }

  [Test]
  public async Task MovesTo_BetweenTheEmptyMapAndAMap_IsEveryPartitionAsync() {
    var map = PartitionMap.Initial([_a]);

    await Assert.That(PartitionMap.Empty.MovesTo(map)).IsEqualTo(P);
    await Assert.That(map.MovesTo(PartitionMap.Empty)).IsEqualTo(P);
    await Assert.That(PartitionMap.Empty.MovesTo(PartitionMap.Empty)).IsEqualTo(0);
  }

  // --- properties over seeded random histories -------------------------------------------------------

  [Test]
  [Arguments(1)]
  [Arguments(2)]
  [Arguments(3)]
  public async Task EveryJoinOrLeave_MovesAtMostCeilPOverN_IsBalancedWithinOne_AndMovesOnlyTheChangedMembersShareAsync(int seed) {
    var random = new Random(seed);
    var members = new List<Guid> { _member(random) };
    var map = PartitionMap.Initial(members);
    for (var step = 0; step < 60; step++) {
      var join = members.Count == 1 || (members.Count < 40 && random.Next(2) == 0);
      var changed = join ? _member(random) : members[random.Next(members.Count)];
      var before = map;
      var n = members.Count;
      if (join) {
        members.Insert(random.Next(members.Count + 1), changed);
      } else {
        members.Remove(changed);
      }

      map = map.Rebalance(members);

      var moved = Enumerable.Range(0, P).Where(p => map.OwnerOf(p) != before.OwnerOf(p)).ToList();
      await Assert.That(moved.Count).IsLessThanOrEqualTo(_ceil(n))
        .Because($"step {step}: a {(join ? "join" : "leave")} from {n} members moves at most ceil(P/N)");
      await Assert.That(join ? moved.All(p => map.OwnerOf(p) == changed) : moved.All(p => before.OwnerOf(p) == changed)).IsTrue()
        .Because($"step {step}: nothing moves but the changed member's share");
      var counts = members.ConvertAll(map.CountOf);
      await Assert.That(counts.Max() - counts.Min()).IsLessThanOrEqualTo(1).Because($"step {step}: balanced within one");
      await Assert.That(counts.Sum()).IsEqualTo(P).Because($"step {step}: every partition has exactly one member");
      var shifts = members.Where(m => m != changed).Select(m => map.CountOf(m) - before.CountOf(m)).ToList();
      await Assert.That(shifts.Max() - shifts.Min()).IsLessThanOrEqualTo(1)
        .Because($"step {step}: the share is taken from (or spread over) the other members evenly");
    }
  }

  [Test]
  public async Task TheSameHistory_GivesTheSameMap_EveryTimeAsync() {
    static PartitionMap Replay() {
      var random = new Random(42);
      var members = new List<Guid>();
      var map = PartitionMap.Empty;
      for (var step = 0; step < 40; step++) {
        if (members.Count < 2 || random.Next(3) > 0) {
          members.Add(_member(random));
        } else {
          members.RemoveAt(random.Next(members.Count));
        }
        map = map.Rebalance(members);
      }
      return map;
    }

    var first = Replay();
    var second = Replay();

    await Assert.That(first.Owners).IsEquivalentTo(second.Owners, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(first.Members).IsEquivalentTo(second.Members, TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task AJoinAndALeaveTogether_StayBalanced_AndMoveOnlyTheLeaversShareAndTheJoinersAsync() {
    var before = PartitionMap.Initial([_a, _b]);

    var after = before.Rebalance([_a, _c]);

    await Assert.That(after.CountOf(_c)).IsEqualTo(P / 2);
    await Assert.That(after.MovesTo(before)).IsEqualTo(before.CountOf(_b))
      .Because("the joiner takes exactly the leaver's share, so the staying member keeps every partition it had");
  }

  /// <summary>A member id drawn from the seeded generator, so a history replays exactly.</summary>
  private static Guid _member(Random random) {
    var bytes = new byte[16];
    random.NextBytes(bytes);
    return new Guid(bytes);
  }
}
