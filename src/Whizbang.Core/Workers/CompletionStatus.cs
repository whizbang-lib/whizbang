// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>
/// Tracks completion status for acknowledgement-before-clear pattern.
/// Items transition: Pending → Sent → Acknowledged → (cleared)
/// </summary>
public enum CompletionStatus {
  /// <summary>In memory, not yet handed to the completion channels</summary>
  Pending = 0,

  /// <summary>Handed to the completion channels, awaiting acknowledgement</summary>
  Sent = 1,

  /// <summary>Acknowledged by the perspective worker, ready to clear</summary>
  Acknowledged = 2
}
