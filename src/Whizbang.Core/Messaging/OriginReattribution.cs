// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// The next batch of the re-attribution walk (#2008), as returned by
/// <see cref="IWorkCoordinator.GetOriginReattributionWindowAsync"/>: the rows after the walk's cursor by commit
/// sequence, through <see cref="ThroughCommitSequence"/>, and the distinct types among them that carry no origin.
/// </summary>
/// <docs>resilience/stream-integrity#re-attribution</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OriginReattributionSqlTests.cs</tests>
public sealed record OriginReattributionWindow {
  /// <summary>The walk's cursor: every row at or below it has been walked (exclusive floor of this batch).</summary>
  public required long AfterCommitSequence { get; init; }

  /// <summary>The last commit sequence in this batch (inclusive); equal to <see cref="AfterCommitSequence"/> when the
  /// walk has caught up.</summary>
  public required long ThroughCommitSequence { get; init; }

  /// <summary>The distinct stored event types in this batch with no origin recorded.</summary>
  public IReadOnlyList<string> EventTypes { get; init; } = [];
}

/// <summary>
/// One non-owned event type of a re-attribution batch and its owner from the ownership map (#2008). A null
/// <see cref="OwnerServiceId"/> means no owner is known: the rows are counted as unattributed and left.
/// </summary>
/// <docs>resilience/stream-integrity#re-attribution</docs>
public sealed record OriginReattributionTarget {
  /// <summary>The stored event type.</summary>
  public required string EventType { get; init; }

  /// <summary>The owner's service id, or null when no owner is known.</summary>
  public Guid? OwnerServiceId { get; init; }

  /// <summary>The owner's service name, cross-checked against each row's stored first hop.</summary>
  public string? OwnerServiceName { get; init; }
}

/// <summary>
/// One re-attribution batch handed to <see cref="IWorkCoordinator.ReattributeOriginsAsync"/> (#2008): the window it
/// came from and the non-owned types in it with their owners. Types not listed are owned and left in the local lane.
/// </summary>
/// <docs>resilience/stream-integrity#re-attribution</docs>
public sealed record OriginReattributionBatch {
  /// <summary>The window's exclusive floor; the batch applies only if the walk's cursor still reads it.</summary>
  public required long AfterCommitSequence { get; init; }

  /// <summary>The window's inclusive ceiling; the cursor moves to it.</summary>
  public required long ThroughCommitSequence { get; init; }

  /// <summary>The non-owned types of the window and their owners.</summary>
  public IReadOnlyList<OriginReattributionTarget> Targets { get; init; } = [];
}

/// <summary>What one re-attribution batch did (#2008).</summary>
/// <docs>resilience/stream-integrity#re-attribution</docs>
public sealed record OriginReattributionResult {
  /// <summary>Rows that took their owner as origin and moved to the owner's digest lane.</summary>
  public int Reattributed { get; init; }

  /// <summary>Rows left because their stored first hop names a service other than the owner.</summary>
  public int HopDisagreements { get; init; }

  /// <summary>Rows of a non-owned type with no known owner, left out of the local lane only logically.</summary>
  public int Unattributed { get; init; }

  /// <summary>False when the walk's cursor no longer read the batch's floor, so nothing was applied.</summary>
  public bool Advanced { get; init; }
}
