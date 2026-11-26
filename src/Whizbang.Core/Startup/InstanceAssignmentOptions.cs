// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;

namespace Whizbang.Core.Startup;

/// <summary>
/// How this instance watches its own assignment (#2595): how often it reads it, and how long it may await its share of
/// the partitions before the <c>assignment</c> health component says so. Bound from
/// <c>Whizbang:Database:InstanceAssignment</c>. Readiness never waits for a share; the grace only decides when a missing
/// one reads Degraded.
/// </summary>
/// <docs>operations/configuration/configuration-reference#instance-assignment</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Startup/InstanceAssignmentMonitorTests.cs</tests>
public sealed class InstanceAssignmentOptions {
  /// <summary>How often the instance reads its assignment. Default 30 seconds.</summary>
  public TimeSpan ObserveInterval { get; set; } = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How long an instance eligible for work may own no partition before the <c>assignment</c> health component reports it
  /// Degraded. Default <see cref="TimeSpan.Zero"/>: derived, two partition-assigner settle windows plus one role renew
  /// interval (<see cref="GraceFor"/>), which is how long a healthy assigner takes to give a new member its share.
  /// </summary>
  public TimeSpan AwaitingShareGrace { get; set; } = TimeSpan.Zero;

  /// <summary>
  /// The grace in effect: <see cref="AwaitingShareGrace"/> when set, else two settle windows plus one renew interval.
  /// </summary>
  /// <param name="settleWindow">The partition assigner's settle window.</param>
  /// <param name="renewInterval">The role renew interval.</param>
  /// <returns>The grace.</returns>
  public TimeSpan GraceFor(TimeSpan settleWindow, TimeSpan renewInterval) =>
    AwaitingShareGrace > TimeSpan.Zero ? AwaitingShareGrace : (settleWindow * 2) + renewInterval;

  /// <summary>Throws when the options describe a configuration that cannot work.</summary>
  /// <exception cref="ArgumentOutOfRangeException">A non-positive observe interval, or a negative grace.</exception>
  public void Validate() {
    if (ObserveInterval <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(ObserveInterval), ObserveInterval, "The observe interval must be positive.");
    }
    if (AwaitingShareGrace < TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(AwaitingShareGrace), AwaitingShareGrace,
        "The awaiting-share grace cannot be negative; zero derives it from the settle window and the renew interval.");
    }
  }
}
