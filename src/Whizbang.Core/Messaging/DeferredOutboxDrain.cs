// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// The one drain of the <see cref="IDeferredOutboxChannel"/> into a strategy's outbox queue, shared by every
/// work coordinator strategy's flush (#2228).
/// </summary>
/// <remarks>
/// The channel's contract is that the next flush writes what was queued on it: the event-audit decorator queues
/// an audit record there for every audited direct append, and the dispatcher queues an event there when no
/// strategy is active. Draining used to live in the Immediate strategy alone, so under the default Scoped
/// strategy (and Interval and Batch) those records were never stored and the singleton channel grew for the
/// life of the process. Every strategy now calls this one method at the start of its flush, before its
/// empty-queue check, and the factory supplies each of them the channel.
/// </remarks>
/// <docs>fundamentals/dispatcher/message-cascade#deferred-event-channel</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/DeferredChannelDrainEveryStrategyTests.cs</tests>
internal static class DeferredOutboxDrain {
  /// <summary>
  /// Moves every message pending on <paramref name="channel"/> to the front of <paramref name="outbox"/>, in the
  /// order they were queued, running each through <paramref name="stamp"/> when one is given. The deferred
  /// messages were queued before the flush's own work, so they are written first.
  /// </summary>
  /// <returns>The number of messages drained; zero when there is no channel or nothing is pending.</returns>
  internal static int PrependTo(
      IDeferredOutboxChannel? channel,
      List<OutboxMessage> outbox,
      Func<OutboxMessage, OutboxMessage>? stamp = null) {
    if (channel?.HasPending != true) {
      return 0;
    }
    var deferred = channel.DrainAll();
    var index = 0;
    foreach (var message in deferred) {
      outbox.Insert(index++, stamp is null ? message : stamp(message));
    }
    return deferred.Count;
  }
}
