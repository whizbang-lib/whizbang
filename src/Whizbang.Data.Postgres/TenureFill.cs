// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// How both PostgreSQL drivers' coordinators fill the partition epochs into a write from the process's tenure ledger
/// (#2556), so every completion, failure, cursor and handler commit a worker makes names the epoch its work was claimed
/// under, though the worker passed none. An epoch the caller named is kept. Without a ledger, or for work the ledger does
/// not know, nothing is filled and the write is not fenced.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorNamesTheClaimedEpochTests.cs</tests>
public static class TenureFill {
  /// <summary>The epochs of <paramref name="ids"/> by position: the caller's, else the ledger's; null when none is known.</summary>
  /// <param name="tenures">The ledger, when registered.</param>
  /// <param name="ids">The work ids.</param>
  /// <param name="given">The epochs the caller named, if any.</param>
  /// <returns>The epochs, or null.</returns>
  public static IReadOnlyList<long?>? Epochs(PartitionTenures? tenures, IReadOnlyList<Guid> ids, IReadOnlyList<long?>? given) {
    ArgumentNullException.ThrowIfNull(ids);
    if (given is not null || tenures is null) {
      return given;
    }
    var epochs = new long?[ids.Count];
    var any = false;
    for (var i = 0; i < ids.Count; i++) {
      epochs[i] = tenures.OfWork(ids[i])?.Epoch;
      any |= epochs[i] is not null;
    }
    return any ? epochs : null;
  }

  /// <summary>Each cursor with its stream's tenure, unless it names one or the ledger knows none.</summary>
  /// <param name="tenures">The ledger, when registered.</param>
  /// <param name="cursors">The cursors.</param>
  /// <returns>The cursors to write.</returns>
  public static IReadOnlyList<PerspectiveCursorCompletion> Cursors(PartitionTenures? tenures, IReadOnlyList<PerspectiveCursorCompletion> cursors) {
    ArgumentNullException.ThrowIfNull(cursors);
    if (tenures is null) {
      return cursors;
    }
    return [.. cursors.Select(c => c.PartitionEpoch is null && tenures.OfStream(c.StreamId) is { } tenure
      ? c with { PartitionNumber = tenure.Partition, PartitionEpoch = tenure.Epoch }
      : c)];
  }

  /// <summary>Each failure with its work's tenure, unless it names one or the ledger knows none.</summary>
  /// <param name="tenures">The ledger, when registered.</param>
  /// <param name="failures">The failures.</param>
  /// <returns>The failures to write.</returns>
  public static IReadOnlyList<MessageFailure> Failures(PartitionTenures? tenures, IReadOnlyList<MessageFailure> failures) {
    ArgumentNullException.ThrowIfNull(failures);
    if (tenures is null) {
      return failures;
    }
    return [.. failures.Select(f => f.PartitionEpoch is null && tenures.OfWork(f.MessageId) is { } tenure
      ? f with { PartitionNumber = tenure.Partition, PartitionEpoch = tenure.Epoch }
      : f)];
  }

  /// <summary>A handler commit naming its inbox message's tenure, unless it names one or the ledger knows none.</summary>
  /// <param name="tenures">The ledger, when registered.</param>
  /// <param name="request">The commit.</param>
  /// <returns>The commit to write.</returns>
  public static HandlerCommitRequest Handler(PartitionTenures? tenures, HandlerCommitRequest request) {
    ArgumentNullException.ThrowIfNull(request);
    return request.PartitionEpoch is null && tenures?.OfWork(request.InboxCompletion.MessageId) is { } tenure
      ? request with { PartitionEpoch = tenure.Epoch }
      : request;
  }

  /// <summary>Records what a claim returned.</summary>
  /// <param name="tenures">The ledger, when registered.</param>
  /// <param name="batch">The claim.</param>
  /// <returns><paramref name="batch"/>.</returns>
  public static WorkBatch RecordClaim(PartitionTenures? tenures, WorkBatch batch) {
    ArgumentNullException.ThrowIfNull(batch);
    tenures?.RecordClaim(batch.StreamEpochs, batch.StreamPartitions);
    return batch;
  }

  /// <summary>Records each fetched piece of work under its stream's tenure.</summary>
  /// <typeparam name="T">The fetched row type.</typeparam>
  /// <param name="tenures">The ledger, when registered.</param>
  /// <param name="rows">The fetched rows.</param>
  /// <param name="workId">The row's work id.</param>
  /// <param name="streamId">The row's stream.</param>
  public static void RecordFetched<T>(PartitionTenures? tenures, IEnumerable<T> rows, Func<T, Guid> workId, Func<T, Guid?> streamId) {
    ArgumentNullException.ThrowIfNull(rows);
    if (tenures is null) {
      return;
    }
    foreach (var row in rows) {
      tenures.RecordFetched(workId(row), streamId(row));
    }
  }
}
