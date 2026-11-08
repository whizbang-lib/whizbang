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
/// <tests>tests/Whizbang.Core.Tests/Messaging/RedeliveryAnswerWindowTests.cs</tests>
public readonly record struct RedeliveryAnswerWindow(long? FromCommitSequence, long? ToCommitSequence) {
  /// <summary>The answer covers the stream's whole history: neither bound is set.</summary>
  public bool IsWholeHistory => FromCommitSequence is null && ToCommitSequence is null;
}
