// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>The explicit partition-to-member map the assigner publishes (#2558). RED stub.</summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#incremental-map</docs>
public sealed class PartitionMap {
  /// <summary>The fixed partition count.</summary>
  public const int PARTITION_COUNT = 10_000;

  private readonly Guid[] _members = [];
  private readonly short[] _owners = [];

  private PartitionMap() { }

  /// <summary>The map with no members.</summary>
  public static PartitionMap Empty { get; } = new();

  /// <summary>The members.</summary>
  public IReadOnlyList<Guid> Members => _members;

  /// <summary>The owners.</summary>
  public IReadOnlyList<short> Owners => _owners;

  /// <summary>The first map.</summary>
  /// <param name="members">The members.</param>
  /// <returns>The map.</returns>
  public static PartitionMap Initial(IReadOnlyList<Guid> members) => throw new NotImplementedException();

  /// <summary>The published map.</summary>
  /// <param name="published">The published assignment.</param>
  /// <returns>The map.</returns>
  public static PartitionMap FromPublished(PartitionAssignment? published) => throw new NotImplementedException();

  /// <summary>The next map.</summary>
  /// <param name="members">The members.</param>
  /// <returns>The map.</returns>
  public PartitionMap Rebalance(IReadOnlyList<Guid> members) => members.Count > _members.Length ? this : Empty;

  /// <summary>The owner.</summary>
  /// <param name="partitionNumber">The partition.</param>
  /// <returns>The owner.</returns>
  public Guid OwnerOf(int partitionNumber) => _members.Length > partitionNumber ? _members[0] : Guid.Empty;

  /// <summary>The count.</summary>
  /// <param name="member">The member.</param>
  /// <returns>The count.</returns>
  public int CountOf(Guid member) => member == Guid.Empty ? 0 : _owners.Length;

  /// <summary>The moves.</summary>
  /// <param name="next">The next map.</param>
  /// <returns>The moves.</returns>
  public int MovesTo(PartitionMap next) => next._owners.Length - _owners.Length;
}
