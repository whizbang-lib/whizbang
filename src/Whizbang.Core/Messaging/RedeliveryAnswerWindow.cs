// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// The window of origin commit sequences a redelivery answer covers (#2143), in the request's units: the events after
/// <see cref="FromCommitSequence"/> (exclusive; null = from the beginning) up to <see cref="ToCommitSequence"/>
/// (inclusive; null = to the head). The origin echoes it on every bundle it answers with, so the receiver can tell a
/// bucket whose deficit the answer covered from one whose missing events lie outside it.
/// </summary>
/// <remarks>
/// A bundle that stored nothing marks a bucket as one repair cannot change only when its window covers the bucket's
/// compared window (a whole-history bucket needs a whole-history answer), or when it carried the event the bucket is
/// missing (#2103). An answer for a recent window carries only the stream's recent events, which the receiver may well
/// hold already while older ones are still missing; marking such a bucket stopped repair on a real gap.
/// </remarks>
/// <param name="FromCommitSequence">Exclusive floor; null = from the beginning.</param>
/// <param name="ToCommitSequence">Inclusive ceiling; null = to the head.</param>
/// <docs>resilience/stream-integrity#an-answer-marks-only-what-it-covered</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RedeliveryAnswerCoverageTests.cs</tests>
public readonly record struct RedeliveryAnswerWindow(long? FromCommitSequence, long? ToCommitSequence) {
  /// <summary>The answer covers the stream's whole history: neither bound is set.</summary>
  public bool IsWholeHistory => FromCommitSequence is null && ToCommitSequence is null;

  /// <summary>
  /// The streams the answer carries only in part (#2149): a stream split across the answer's bundles, running across a
  /// page of the origin's selection, or stopped at the origin's cap. Its missing events may lie in a part this bundle did
  /// not carry, so a bundle that changed nothing never marks such a stream's bucket, or its type's lane, as one repair
  /// cannot change, whatever its window. Null (or empty) when every stream was carried whole.
  /// </summary>
  /// <docs>resilience/stream-integrity#an-answer-marks-only-what-it-covered</docs>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RedeliveryAnswerCoverageTests.PartialStreams.cs</tests>
  public IReadOnlyList<Guid>? PartialStreamIds { get; init; }

  /// <summary>
  /// More bundles of the same answer follow this one (#2311). A whole-type answer is judged as a whole: a bundle that
  /// changed nothing and says the answer continues leaves the type lane awaiting the rest, so only the answer's last
  /// bundle can mark it as one repair cannot change, and only when no bundle before it brought anything. False on the
  /// answer's last bundle, and from an origin that predates the field, whose every bundle is judged on its own as before.
  /// </summary>
  /// <docs>resilience/stream-integrity#a-whole-type-answer-is-judged-as-a-whole</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityTypeLaneAnswerSqlTests.WholeAnswer.cs</tests>
  public bool Continues { get; init; }
}
