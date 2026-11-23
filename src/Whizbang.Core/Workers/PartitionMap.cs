// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>
/// The explicit partition-to-member map the elected assigner publishes (#2558): which member each of the
/// <see cref="PARTITION_COUNT"/> partitions belongs to. The assigner keeps it and changes it incrementally, so a join
/// or leave moves about 1/N of the partitions and nothing else moves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Representation.</b> One entry per partition, the zero-based index of its member in <see cref="Members"/>:
/// a <c>SMALLINT[10000]</c> in the published row. Every partition has exactly one member by construction, so no
/// partition can be given to two members or to none; a claim answers "is this mine" with one array subscript; the size
/// is a fixed 20,000 bytes whatever the member count; and both SQL and Npgsql read it with no codec. A member index fits
/// a <see cref="short"/> up to 32,767 members.
/// </para>
/// <para>
/// <b>The first map is the modulo.</b> With no map to start from, partition <c>p</c> belongs to member
/// <c>p % N</c>, which is the arithmetic every claim applied before the map existed, so the first publish of a map moves
/// nothing.
/// </para>
/// <para>
/// <b>Minimal moves.</b> <see cref="Rebalance"/> gives every member a quota of <c>P / N</c> partitions, one more for
/// the <c>P % N</c> members that already hold the most (ties by position), so the map is balanced within one. Each
/// staying member keeps its lowest-numbered partitions up to its quota; the partitions of a member that left, and a
/// staying member's partitions beyond its quota, go to the members below theirs, in member order. A join therefore
/// takes about <c>P / (N + 1)</c> partitions, within one of evenly from each member, and a leave spreads the leaver's
/// partitions within one of evenly over the rest; either moves at most <c>ceil(P / N)</c>. The result depends only on
/// the map and the new members, so the same history always gives the same map.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/partition-assignment#incremental-map</docs>
/// <tests>tests/Whizbang.Partitioning.Tests/PartitionMapTests.cs:EveryJoinOrLeave_MovesAtMostCeilPOverN_IsBalancedWithinOne_AndMovesOnlyTheChangedMembersShareAsync</tests>
/// <tests>tests/Whizbang.Partitioning.Tests/PartitionMapTests.cs:TheSameHistory_GivesTheSameMap_EveryTimeAsync</tests>
/// <tests>tests/Whizbang.Partitioning.Tests/PartitionMapTests.cs:Initial_IsTheModuloOverTheMembers_SoTheFirstMapMovesNothingAsync</tests>
public sealed class PartitionMap {
  /// <summary>
  /// The number of partitions, fixed (owner decision, 2026-11-23): changing it would re-partition every stream, which is
  /// a migration of its own.
  /// </summary>
  public const int PARTITION_COUNT = 10_000;

  private readonly Guid[] _members;
  private readonly short[] _owners;

  private PartitionMap(Guid[] members, short[] owners) {
    _members = members;
    _owners = owners;
  }

  /// <summary>The map with no members, which owns no partition.</summary>
  public static PartitionMap Empty { get; } = new([], []);

  /// <summary>The members, in the order the owner indexes refer to.</summary>
  public IReadOnlyList<Guid> Members => _members;

  /// <summary>
  /// For each partition, the zero-based index in <see cref="Members"/> of the member it belongs to:
  /// <see cref="PARTITION_COUNT"/> entries, or none for <see cref="Empty"/>.
  /// </summary>
  public IReadOnlyList<short> Owners => _owners;

  /// <summary>The first map for <paramref name="members"/>: partition <c>p</c> belongs to member <c>p % N</c>.</summary>
  /// <param name="members">The members, in order; one listed twice is one member.</param>
  /// <returns>The map, or <see cref="Empty"/> when there are no members.</returns>
  /// <exception cref="OverflowException">More than 32,767 members.</exception>
  public static PartitionMap Initial(IReadOnlyList<Guid> members) {
    ArgumentNullException.ThrowIfNull(members);
    Guid[] distinct = [.. members.Distinct()];
    if (distinct.Length == 0) {
      return Empty;
    }
    var owners = new short[PARTITION_COUNT];
    for (var p = 0; p < PARTITION_COUNT; p++) {
      owners[p] = checked((short)(p % distinct.Length));
    }
    return new PartitionMap(distinct, owners);
  }

  /// <summary>
  /// The map a published assignment states: its map when it carries one of <see cref="PARTITION_COUNT"/> entries,
  /// otherwise the modulo over its members its claimers apply (an assignment of the older shape).
  /// </summary>
  /// <param name="published">The published assignment, or null when none is.</param>
  /// <returns>The map; <see cref="Empty"/> when nothing is published.</returns>
  public static PartitionMap FromPublished(PartitionAssignment? published) {
    if (published is null) {
      return Empty;
    }
    return published.Owners is { Count: PARTITION_COUNT } owners
      ? new PartitionMap([.. published.Members], [.. owners])
      : Initial(published.Members);
  }

  /// <summary>
  /// The next map for <paramref name="members"/>, changed from this one as little as balance allows. See the remarks.
  /// </summary>
  /// <param name="members">The members, in order; one listed twice is one member.</param>
  /// <returns>The next map; <see cref="Empty"/> when there are no members.</returns>
  /// <exception cref="OverflowException">More than 32,767 members.</exception>
  public PartitionMap Rebalance(IReadOnlyList<Guid> members) {
    ArgumentNullException.ThrowIfNull(members);
    Guid[] next = [.. members.Distinct()];
    if (_owners.Length == 0 || next.Length == 0) {
      return Initial(next);
    }
    var index = new Dictionary<Guid, int>(next.Length);
    for (var i = 0; i < next.Length; i++) {
      index[next[i]] = i;
    }
    // Where each partition's current member sits in the next membership; -1 for a member that left.
    var current = new int[PARTITION_COUNT];
    var held = new int[next.Length];
    for (var p = 0; p < PARTITION_COUNT; p++) {
      current[p] = index.GetValueOrDefault(_members[_owners[p]], -1);
      if (current[p] >= 0) {
        held[current[p]]++;
      }
    }
    var quota = _quotas(held);
    var owners = new short[PARTITION_COUNT];
    var kept = new int[next.Length];
    var free = new List<int>();
    for (var p = 0; p < PARTITION_COUNT; p++) {
      var member = current[p];
      if (member >= 0 && kept[member] < quota[member]) {
        owners[p] = (short)member;
        kept[member]++;
      } else {
        free.Add(p);
      }
    }
    var taken = 0;
    for (var member = 0; member < next.Length; member++) {
      for (; kept[member] < quota[member]; kept[member]++) {
        owners[free[taken++]] = checked((short)member);
      }
    }
    return new PartitionMap(next, owners);
  }

  /// <summary>
  /// Each member's quota: <c>P / N</c>, one more for the <c>P % N</c> members holding the most now (ties by position),
  /// so the members that already hold the extra partition keep it and nothing moves to rebalance it.
  /// </summary>
  private static int[] _quotas(int[] held) {
    var quota = new int[held.Length];
    Array.Fill(quota, PARTITION_COUNT / held.Length);
    var mostFirst = Enumerable.Range(0, held.Length).OrderByDescending(i => held[i]).ThenBy(i => i);
    foreach (var member in mostFirst.Take(PARTITION_COUNT % held.Length)) {
      quota[member]++;
    }
    return quota;
  }

  /// <summary>The member a partition belongs to. A partition past the map wraps, as the claim's predicate does.</summary>
  /// <param name="partitionNumber">A partition number, zero or more.</param>
  /// <returns>Its member.</returns>
  /// <exception cref="InvalidOperationException">The map has no members.</exception>
  public Guid OwnerOf(int partitionNumber) {
    ArgumentOutOfRangeException.ThrowIfNegative(partitionNumber);
    if (_owners.Length == 0) {
      throw new InvalidOperationException("The map has no members.");
    }
    return _members[_owners[partitionNumber % PARTITION_COUNT]];
  }

  /// <summary>How many partitions <paramref name="member"/> holds; zero when it is not a member.</summary>
  /// <param name="member">The instance.</param>
  /// <returns>Its partition count.</returns>
  public int CountOf(Guid member) {
    var i = Array.IndexOf(_members, member);
    return i < 0 ? 0 : _owners.Count(o => o == i);
  }

  /// <summary>
  /// How many partitions change member between this map and <paramref name="next"/>. Between a map and
  /// <see cref="Empty"/>, every partition does.
  /// </summary>
  /// <param name="next">The other map.</param>
  /// <returns>The number of partitions whose member differs.</returns>
  public int MovesTo(PartitionMap next) {
    ArgumentNullException.ThrowIfNull(next);
    if (_owners.Length == 0 || next._owners.Length == 0) {
      return _owners.Length == next._owners.Length ? 0 : PARTITION_COUNT;
    }
    var moves = 0;
    for (var p = 0; p < PARTITION_COUNT; p++) {
      if (_members[_owners[p]] != next._members[next._owners[p]]) {
        moves++;
      }
    }
    return moves;
  }
}
