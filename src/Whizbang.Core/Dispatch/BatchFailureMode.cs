// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Dispatch;

/// <summary>
/// What a batch call (<c>SendManyAsync</c>, <c>PublishManyAsync</c>, <c>LocalSendManyAsync</c>) does
/// when one message's local handler fails, set through <see cref="DispatchOptions.BatchFailure"/>.
/// In both modes the failed message's own outbox copy is still written.
/// </summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#batches</docs>
public enum BatchFailureMode {
  /// <summary>
  /// Stop at the first failure (the default): messages already handled keep their effects, the rest
  /// are neither handled nor queued, and the failure surfaces.
  /// </summary>
  Stop = 0,

  /// <summary>Handle and queue every message, then throw an <see cref="AggregateException"/> listing the failures.</summary>
  ContinueAndAggregate = 1,
}
