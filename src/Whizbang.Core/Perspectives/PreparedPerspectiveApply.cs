// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Observability;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Why a prepared (stream, perspective) pair is handed back to the per-pair path instead of being written with its
/// batch.
/// </summary>
/// <remarks>
/// Each reason is a case the per-pair path already handles and the set-based write does not repeat: it needs a
/// replay of the stream, or state the batch write cannot express. The per-pair path stays the one place those cases
/// are decided.
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#prepare-and-commit</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PreparedPerspectiveApplyTests.cs</tests>
public enum PerspectivePerPairReason {
  /// <summary>The pair takes the batch write (or has nothing to write).</summary>
  None = 0,

  /// <summary>The runner does not prepare writes (a hand-written runner, which keeps the default).</summary>
  RunnerDoesNotPrepare = 1,

  /// <summary>An event of the batch sits below the row's recorded position, so the stream is re-folded (#1347).</summary>
  RewindBelowPosition = 2,

  /// <summary>
  /// The row is missing but its stream has earlier history: an evicted or archived row whose stream woke, which is
  /// rebuilt by a replay (#1483, #1739).
  /// </summary>
  MissingRowWithHistory = 3,

  /// <summary>The row is missing because the stream was purged, and stays purged unless an apply resurrects it (#1027).</summary>
  PurgedStream = 4,

  /// <summary>The store could not prepare the write (another provider, or a model or column it cannot write set-based).</summary>
  WriteNotPreparable = 5,
}

/// <summary>
/// One (stream, perspective) pair, folded and waiting for its write: the result of
/// <see cref="IPerspectiveRunner.PrepareAsync"/>, handed back to <see cref="IPerspectiveRunner.CompleteAsync"/> with
/// the outcome of <see cref="Write"/>.
/// </summary>
/// <remarks>
/// <para>
/// A prepared pair is one of three things. It carries a <see cref="Write"/> for the batch's write sink; it has nothing
/// to write (<see cref="Write"/> null and <see cref="PerPairReason"/> <see cref="PerspectivePerPairReason.None"/>,
/// such as a batch the row already holds); or it runs the per-pair path (<see cref="RunsPerPair"/>), which
/// <see cref="IPerspectiveRunner.CompleteAsync"/> does under the locks the caller already holds.
/// </para>
/// <para>
/// A generated runner derives from this type to carry the state its completion needs (the folded model, the events
/// it folded, the snapshot taken before the write), so a prepared pair is completed by the runner that prepared it.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#prepare-and-commit</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PreparedPerspectiveApplyTests.cs</tests>
public class PreparedPerspectiveApply {
  /// <summary>Creates a prepared pair.</summary>
  /// <param name="streamId">The stream.</param>
  /// <param name="perspectiveName">The perspective.</param>
  /// <param name="lastProcessedEventId">The cursor position the batch was claimed after.</param>
  /// <param name="events">The batch's events, in the order they are applied.</param>
  /// <param name="write">The write for the batch's sink, or null.</param>
  /// <param name="perPairReason">Why the pair runs the per-pair path, or <see cref="PerspectivePerPairReason.None"/>.</param>
  /// <exception cref="ArgumentException">A write is given for a pair that runs the per-pair path.</exception>
  protected PreparedPerspectiveApply(
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      IReadOnlyList<MessageEnvelope<IEvent>> events,
      PendingPerspectiveWrite? write,
      PerspectivePerPairReason perPairReason) {
    StreamId = streamId;
    PerspectiveName = perspectiveName;
    LastProcessedEventId = lastProcessedEventId;
    Events = events;
    Write = write;
    PerPairReason = perPairReason;
  }

  /// <summary>The stream.</summary>
  public Guid StreamId { get; }

  /// <summary>The perspective.</summary>
  public string PerspectiveName { get; }

  /// <summary>The cursor position the batch was claimed after.</summary>
  public Guid? LastProcessedEventId { get; }

  /// <summary>The batch's events, in the order they are applied.</summary>
  public IReadOnlyList<MessageEnvelope<IEvent>> Events { get; }

  /// <summary>The write for the batch's sink, or null when there is nothing to write or the pair runs per-pair.</summary>
  public PendingPerspectiveWrite? Write { get; }

  /// <summary>Why the pair runs the per-pair path, or <see cref="PerspectivePerPairReason.None"/>.</summary>
  public PerspectivePerPairReason PerPairReason { get; }

  /// <summary>Whether the pair runs the per-pair path when it is completed.</summary>
  public bool RunsPerPair => PerPairReason != PerspectivePerPairReason.None;

  /// <summary>A pair that runs the per-pair path when it is completed.</summary>
  /// <param name="streamId">The stream.</param>
  /// <param name="perspectiveName">The perspective.</param>
  /// <param name="lastProcessedEventId">The cursor position the batch was claimed after.</param>
  /// <param name="events">The batch's events.</param>
  /// <param name="reason">Why; never <see cref="PerspectivePerPairReason.None"/>.</param>
  /// <returns>The prepared pair.</returns>
  public static PreparedPerspectiveApply PerPair(
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      IReadOnlyList<MessageEnvelope<IEvent>> events,
      PerspectivePerPairReason reason) {
    return new PreparedPerspectiveApply(streamId, perspectiveName, lastProcessedEventId, events, write: null, reason);
  }
}
