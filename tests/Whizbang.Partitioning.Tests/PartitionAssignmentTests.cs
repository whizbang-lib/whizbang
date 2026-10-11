// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Partitioning.Tests;

/// <summary>The published assignment's arithmetic and the connection-mode spelling (#1254).</summary>
[Category("Workers")]
public class PartitionAssignmentTests {
  private static readonly Guid _a = new("00000000-0000-0000-0000-00000000000a");
  private static readonly Guid _b = new("00000000-0000-0000-0000-00000000000b");
  private static readonly Guid _c = new("00000000-0000-0000-0000-00000000000c");

  private static PartitionAssignment _assignment(params Guid[] members) =>
    new(Epoch: 4, Revision: 2, AssignerInstanceId: _a, Members: members,
      PublishedAt: DateTimeOffset.UnixEpoch, LeaseExpiresAt: DateTimeOffset.UnixEpoch.AddMinutes(1));

  [Test]
  public async Task RankOf_AMember_IsItsPositionAsync() {
    var assignment = _assignment(_a, _b, _c);

    await Assert.That(assignment.RankOf(_a)).IsEqualTo(0);
    await Assert.That(assignment.RankOf(_c)).IsEqualTo(2);
  }

  [Test]
  public async Task RankOf_NotAMember_IsNullAsync() {
    await Assert.That(_assignment(_a, _b).RankOf(_c)).IsNull();
  }

  [Test]
  public async Task OwnerOf_APartition_IsTheMemberAtItsResidueAsync() {
    var assignment = _assignment(_a, _b, _c);

    await Assert.That(assignment.OwnerOf(0)).IsEqualTo(_a);
    await Assert.That(assignment.OwnerOf(4)).IsEqualTo(_b);
    await Assert.That(assignment.OwnerOf(9998)).IsEqualTo(_c);
  }

  [Test]
  public async Task OwnerOf_ANegativePartition_IsRefusedAsync() {
    await Assert.That(() => _assignment(_a).OwnerOf(-1)).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task OwnerOf_WithNoMembers_IsRefusedAsync() {
    await Assert.That(() => _assignment().OwnerOf(0)).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task Equality_ComparesTheMembersInOrder_NotTheListAsync() {
    var one = _assignment(_a, _b);
    var same = _assignment([.. new[] { _a, _b }]);

    await Assert.That(one).IsEqualTo(same);
    await Assert.That(one.GetHashCode()).IsEqualTo(same.GetHashCode());
    await Assert.That(one).IsNotEqualTo(_assignment(_b, _a)).Because("rank is the position, so order matters");
    await Assert.That(one.Equals(null)).IsFalse();
  }

  [Test]
  [Arguments(1)]
  [Arguments(2)]
  [Arguments(3)]
  [Arguments(4)]
  [Arguments(5)]
  public async Task Equality_DiffersWhenAnyFieldDiffersAsync(int field) {
    var one = _assignment(_a);
    var other = field switch {
      1 => one with { Epoch = 5 },
      2 => one with { Revision = 3 },
      3 => one with { AssignerInstanceId = _b },
      4 => one with { PublishedAt = one.PublishedAt.AddSeconds(1) },
      _ => one with { LeaseExpiresAt = one.LeaseExpiresAt.AddSeconds(1) },
    };

    await Assert.That(one).IsNotEqualTo(other);
  }

  [Test]
  public async Task Version_IsTheEpochAndRevisionAsync() {
    await Assert.That(_assignment(_a).Version).IsEqualTo(new PartitionAssignmentVersion(4, 2));
  }

  [Test]
  [Arguments(InstanceConnectionMode.Direct, "direct")]
  [Arguments(InstanceConnectionMode.Pooled, "pooled")]
  public async Task ToDatabaseValue_SpellsTheModeAsync(InstanceConnectionMode mode, string expected) {
    await Assert.That(InstanceConnectionModes.ToDatabaseValue(mode)).IsEqualTo(expected);
  }

  [Test]
  [Arguments("direct", InstanceConnectionMode.Direct)]
  [Arguments("pooled", InstanceConnectionMode.Pooled)]
  [Arguments(null, InstanceConnectionMode.Pooled)]
  [Arguments("something-else", InstanceConnectionMode.Pooled)]
  public async Task Parse_AnythingButDirect_IsPooledAsync(string? value, InstanceConnectionMode expected) {
    await Assert.That(InstanceConnectionModes.Parse(value)).IsEqualTo(expected);
  }
}
