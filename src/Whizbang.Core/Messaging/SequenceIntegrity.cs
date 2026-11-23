// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// How long the outbox holds an event for its commit-order stamp before publishing it. Decided only at the origin, and
/// separate from delivery priority: sequence integrity is applied first, and priority orders the messages that are
/// ready.
/// </summary>
/// <remarks>
/// An event's commit sequence (its position in this service's commit order) is written by the commit-order stamper
/// after the event is stored. An event published before that carries no origin position, so a receiver can only order
/// it approximately. A hold trades a little publish latency for a position on every event. A stream with any held
/// event holds its later events too, whatever their own setting, so a receiver never sees a stream out of order.
/// </remarks>
/// <docs>fundamentals/messaging/sequence-integrity</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/SequenceIntegrityPolicyTests.cs</tests>
public enum SequenceIntegrity {
  /// <summary>Publish at once, racing the stamp. The event may arrive without an origin position.</summary>
  None = 0,

  /// <summary>
  /// Hold the event until it is stamped or its hold time passes, then publish it anyway. The out-of-the-box setting.
  /// </summary>
  Bounded = 1,

  /// <summary>Hold the event until it is stamped, however long that takes.</summary>
  UntilStamped = 2,
}
