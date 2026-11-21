// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The operator repair for received streams stored out of their origin's order (#1629): stores each origin's events in
/// that origin's order, in the versions those events already hold, and rewinds the stream's perspectives from its start.
/// </summary>
/// <remarks>
/// <para>
/// Before #1607 a receiver could store one producer stream's events out of the producer's order. Replay already folds
/// each origin's events in origin order, but everything that reads a stream by version still saw the stored order. The
/// order is <see cref="ReceivedStreamOrder"/>'s, the replay's own rule: local events, and the interleaving of different
/// origins, keep their versions; each origin's events are reordered among the versions they hold, independently of
/// every other origin.
/// </para>
/// <para>
/// Safe on a live system: it walks the store a bounded batch of streams at a time, each batch its own transaction; it
/// takes the per-stream lock appends take, never waits for one (a stream being written is reported
/// <see cref="ResequenceSkipReason.Busy"/>), and checks each planned event is still where it was read. A dry run takes no
/// lock and writes nothing. Safe to repeat: a stream already in its origins' order is not touched, so a run stopped or
/// failed part way is finished by running it again.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/resequencing-received-streams</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamResequenceSqlTests.cs</tests>
public interface IReceivedStreamResequencer {
  /// <summary>Finds received streams stored out of their origins' order and, unless a dry run, re-sequences them.</summary>
  /// <param name="request">Who asks, why, how far and whether to change anything.</param>
  /// <param name="cancellationToken">Cancels between batches; a batch already sent completes or rolls back whole.</param>
  /// <returns>What was (or, in a dry run, would be) re-sequenced, and what was left alone.</returns>
  Task<ReceivedStreamResequenceReport> ResequenceAsync(ReceivedStreamResequenceRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A request to re-sequence received streams stored out of their origins' order.</summary>
/// <docs>operations/infrastructure/resequencing-received-streams#running-it</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamResequenceSqlTests.cs:Request_WithoutWhoOrWhyOrAPositiveBound_IsRefusedAsync</tests>
public sealed record ReceivedStreamResequenceRequest {
  /// <summary>The streams one batch (one transaction) examines unless told otherwise.</summary>
  public const int DEFAULT_BATCH_SIZE = 100;

  /// <summary>Who asked, recorded in the log.</summary>
  public required string RequestedBy { get; init; }

  /// <summary>Why, recorded with <see cref="RequestedBy"/>.</summary>
  public required string Reason { get; init; }

  /// <summary>Report what would change and change nothing.</summary>
  public bool DryRun { get; init; }

  /// <summary>The streams one batch examines; each batch is one transaction holding at most this many stream locks.</summary>
  public int BatchSize { get; init; } = DEFAULT_BATCH_SIZE;

  /// <summary>The most streams this run examines; null walks the whole store. The report says where to resume.</summary>
  public int? MaxStreams { get; init; }

  /// <summary>Starts after this stream id (a report's <see cref="ReceivedStreamResequenceReport.ResumeAfterStreamId"/>); null starts at the first stream.</summary>
  public Guid? StartAfterStreamId { get; init; }

  /// <summary>Identifies this run in the log.</summary>
  public Guid RunId { get; init; } = ValueObjects.TrackedGuid.New().Value;

  /// <summary>Refuses a request that does not say who and why, or whose bounds are not positive.</summary>
  /// <exception cref="ArgumentException">The request is incomplete.</exception>
  public void Validate() {
    if (string.IsNullOrWhiteSpace(RequestedBy)) {
      throw new ArgumentException("A re-sequence must say who requested it.", nameof(RequestedBy));
    }
    if (string.IsNullOrWhiteSpace(Reason)) {
      throw new ArgumentException("A re-sequence must say why.", nameof(Reason));
    }
    if (BatchSize < 1) {
      throw new ArgumentException("The batch size must be at least 1.", nameof(BatchSize));
    }
    if (MaxStreams is < 1) {
      throw new ArgumentException("The stream limit must be at least 1 when given.", nameof(MaxStreams));
    }
  }
}

/// <summary>Why an out-of-order stream was left alone.</summary>
/// <docs>operations/infrastructure/resequencing-received-streams#what-is-left-alone</docs>
public enum ResequenceSkipReason {
  /// <summary>Another transaction held the stream's lock (it was being written); run again to re-sequence it.</summary>
  Busy,

  /// <summary>An event moved or went between the read and the write by a path that does not take the stream's lock; run again.</summary>
  Changed,

  /// <summary>The stream holds ephemeral events: their history is reaped, so its perspectives cannot replay it from the start.</summary>
  Ephemeral,

  /// <summary>The stream holds collective events, whose sink queue follows the stored version.</summary>
  Collective,

  /// <summary>Part of the stream's history is archived, and the archive keeps the versions it was written with.</summary>
  Archived,
}

/// <summary>A stream re-sequenced (or, in a dry run, that would be).</summary>
/// <param name="StreamId">The stream.</param>
/// <param name="Moves">Each event whose version changed, from and to.</param>
/// <param name="PerspectivesRewound">Perspectives whose cursor on the stream was flagged to replay it from its start.</param>
/// <param name="SnapshotsRemoved">The stream's snapshots removed, each a fold in the old order.</param>
/// <docs>operations/infrastructure/resequencing-received-streams#what-a-re-sequence-changes</docs>
public sealed record StreamResequence(Guid StreamId, IReadOnlyList<ResequencedEvent> Moves, int PerspectivesRewound, int SnapshotsRemoved);

/// <summary>An out-of-order stream left alone, and why.</summary>
/// <param name="StreamId">The stream.</param>
/// <param name="Reason">Why.</param>
/// <docs>operations/infrastructure/resequencing-received-streams#what-is-left-alone</docs>
public sealed record StreamLeftAlone(Guid StreamId, ResequenceSkipReason Reason);

/// <summary>What a re-sequence run did, or in a dry run would do.</summary>
/// <param name="RunId">The run.</param>
/// <param name="DryRun">Whether anything changed.</param>
/// <param name="StreamsExamined">Streams the run walked.</param>
/// <param name="Resequenced">The streams re-sequenced.</param>
/// <param name="Skipped">Out-of-order streams left alone.</param>
/// <param name="ResumeAfterStreamId">Where a run bounded by <see cref="ReceivedStreamResequenceRequest.MaxStreams"/> stopped; null once the whole store was walked.</param>
/// <docs>operations/infrastructure/resequencing-received-streams#running-it</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReceivedStreamResequenceSqlTests.cs:Report_Format_SaysWhatChangedAndWhatWasLeftAloneAsync</tests>
public sealed record ReceivedStreamResequenceReport(
  Guid RunId,
  bool DryRun,
  int StreamsExamined,
  IReadOnlyList<StreamResequence> Resequenced,
  IReadOnlyList<StreamLeftAlone> Skipped,
  Guid? ResumeAfterStreamId) {

  /// <summary>A summary an operator can read.</summary>
  /// <returns>One line for the run, one per stream re-sequenced, one per stream left alone.</returns>
  public string Format() {
    var lines = new List<string> {
      $"{(DryRun ? "Dry run: would re-sequence" : "Re-sequenced")} {Resequenced.Count} stream(s) of {StreamsExamined} examined "
        + $"({Resequenced.Sum(s => s.Moves.Count)} event(s) moved); {Skipped.Count} left alone; "
        + (ResumeAfterStreamId is { } resume ? $"stopped after {resume}" : "whole store walked") + $"; run {RunId}",
    };
    foreach (var stream in Resequenced) {
      lines.Add($"  {stream.StreamId}: {stream.Moves.Count} moved, {stream.PerspectivesRewound} perspective(s) rewound, "
        + $"{stream.SnapshotsRemoved} snapshot(s) removed");
    }
    foreach (var skipped in Skipped) {
      lines.Add($"  {skipped.StreamId}: left alone ({skipped.Reason})");
    }
    return string.Join(Environment.NewLine, lines);
  }
}
