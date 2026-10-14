// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Workers;

namespace Whizbang.Core.MultiStream;

/// <summary>A request to claim the pending streams of any run that this instance owns.</summary>
/// <param name="InstanceId">The claiming instance.</param>
/// <param name="BatchSize">The most streams to claim at once.</param>
/// <param name="Lease">How long a claimed stream is held before it is offered again.</param>
/// <param name="Assignment">The partition assignment this instance has cached, or null to rank the live instances
/// instead, exactly as the work claim does.</param>
/// <docs>fundamentals/dispatcher/multi-stream-commands#ownership</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:ClaimAsync_UnderAPublishedAssignment_EachInstanceClaimsOnlyItsPartitionShareAsync</tests>
public sealed record MultiStreamClaim(Guid InstanceId, int BatchSize, TimeSpan Lease, PartitionAssignmentVersion? Assignment);

/// <summary>One stream of a run, claimed by this instance to work now.</summary>
/// <param name="RunId">The run.</param>
/// <param name="StreamId">The stream to work.</param>
/// <param name="CommandType">The command's type name, the key of its <see cref="IMultiStreamCommandBinding"/>.</param>
/// <param name="Payload">The command as stored.</param>
/// <param name="Scope">The security scope the command was sent under, or null.</param>
/// <param name="Attempt">Which attempt this is, from 1.</param>
/// <docs>fundamentals/dispatcher/multi-stream-commands#ownership</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:ClaimAsync_ReturnsTheRunsCommandAndScopeWithEachStreamAsync</tests>
public sealed record MultiStreamWorkItem(Guid RunId, Guid StreamId, string CommandType, string Payload, string? Scope, int Attempt);

/// <summary>How working one claimed stream ended.</summary>
/// <param name="RunId">The run.</param>
/// <param name="StreamId">The stream.</param>
/// <param name="Error">Null when the handler succeeded; otherwise the failure (the exception's type and message), or, for
/// a release, why the stream was not worked.</param>
/// <docs>fundamentals/dispatcher/multi-stream-commands#outcomes</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:CompleteAsync_AFailureAtTheAttemptLimit_RecordsTheStreamFailedWithItsErrorAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:CompleteAsync_AReleasedStream_IsOfferedAgainAfterTheDelayWithoutCountingAnAttemptAsync</tests>
public sealed record MultiStreamOutcome(Guid RunId, Guid StreamId, string? Error) {
  /// <summary>
  /// True when the stream was not worked and is handed back without counting the attempt, to be offered again after the
  /// release delay; <see cref="Error"/> says why. Used when this instance has no handler for the command.
  /// </summary>
  public bool Released { get; init; }

  /// <summary>An outcome that hands the stream back without counting the attempt.</summary>
  /// <param name="runId">The run.</param>
  /// <param name="streamId">The stream.</param>
  /// <param name="reason">Why the stream was not worked.</param>
  /// <returns>The release.</returns>
  public static MultiStreamOutcome Release(Guid runId, Guid streamId, string reason) => new(runId, streamId, reason) { Released = true };
}

/// <summary>A run that every one of its streams has now reported on.</summary>
/// <param name="RunId">The run.</param>
/// <param name="CommandType">The command's type name.</param>
/// <param name="StreamCount">How many streams the run named.</param>
/// <param name="Succeeded">How many succeeded.</param>
/// <param name="Failed">How many failed after their last attempt.</param>
/// <docs>fundamentals/dispatcher/multi-stream-commands#outcomes</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:CompleteAsync_TheLastStream_CompletesTheRunOnceAsync</tests>
public sealed record MultiStreamRunCompletion(Guid RunId, string CommandType, int StreamCount, int Succeeded, int Failed);

/// <summary>Where a run stands.</summary>
/// <param name="RunId">The run.</param>
/// <param name="CommandType">The command's type name.</param>
/// <param name="StreamCount">How many streams the run named.</param>
/// <param name="Succeeded">How many succeeded.</param>
/// <param name="Failed">How many failed after their last attempt.</param>
/// <param name="Pending">How many have no outcome yet.</param>
/// <param name="CreatedAt">When the run was recorded.</param>
/// <param name="CompletedAt">When its last stream reported, or null while any is pending.</param>
/// <docs>fundamentals/dispatcher/multi-stream-commands#outcomes</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/MultiStreamCommandStoreSqlTests.cs:RecordRunAsync_ANewRun_IsPendingForEveryStreamAsync</tests>
public sealed record MultiStreamRunStatus(
  Guid RunId, string CommandType, int StreamCount, int Succeeded, int Failed, int Pending,
  DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);
