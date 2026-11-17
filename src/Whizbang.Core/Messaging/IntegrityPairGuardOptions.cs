// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// The pair guard (<c>Whizbang:StreamIntegrity:PairGuard</c>, #2362): integrity runs for a service pair only while both
/// apply the same eligibility rules to the event types they share.
/// </summary>
/// <docs>resilience/stream-integrity#the-pair-guard</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityPairGuardTests.cs</tests>
public sealed class IntegrityPairGuardOptions {
  /// <summary>
  /// How long a pair may stay halted before the guard alerts (<c>Whizbang:StreamIntegrity:PairGuard:HaltedAlertAfter</c>,
  /// default 2 hours, owner decision Q5). A rollout halts its mixed pairs briefly, which is expected; a pair still
  /// halted past this is a rollout that did not finish, or two services built from different rules.
  /// </summary>
  /// <docs>resilience/stream-integrity#the-pair-guard</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityPairGuardTests.cs:Options_AlertAfterTwoHoursByDefaultAsync</tests>
  public TimeSpan HaltedAlertAfter { get; set; } = TimeSpan.FromHours(2);
}
