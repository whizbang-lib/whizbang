// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Dispatch;

/// <summary>
/// Per-call overrides of the dispatch policy's defaults, set through <see cref="DispatchOptions.Behaviors"/>.
/// </summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#behaviors</docs>
[Flags]
public enum DispatchBehaviors {
  /// <summary>Use the policy's defaults.</summary>
  None = 0,

  /// <summary>
  /// Fail whenever this service has no local handler for the message, even where the default would go
  /// ahead (an event kept in this service with no local subscriber, or a message sent on to others).
  /// </summary>
  ThrowWhenNoHandler = 1,

  /// <summary>
  /// Never fail because this service has no local handler. A call that must return the handler's
  /// result still fails, since it has nothing to return.
  /// </summary>
  ContinueWhenNoHandler = 2,

  /// <summary>
  /// Let a message scheduled for later fall back to the call's usual no-outbox behavior when this
  /// service has no outbox, instead of failing. The schedule may then not hold.
  /// </summary>
  AllowScheduleWithoutOutbox = 4,
}
