// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Dispatch;

/// <summary>
/// Marks the lifecycle stages the dispatcher runs for a message it handles in this process. Such a
/// dispatch was already admitted at its entry (refused up front only when
/// <see cref="Security.MessageSecurityOptions.RequireContextForLocalDispatch"/> is set), so a stage after
/// its handler does not refuse it for a missing security context: a refusal after the handler ran would
/// protect nothing (#1419). Messages that arrive from a transport, and stages a worker runs, are not marked.
/// </summary>
/// <remarks>
/// The mark is async-local: set inside the dispatcher's stage call, it covers that call and everything it
/// awaits or starts, including a detached stage's background task, and ends when the call returns.
/// </remarks>
/// <docs>fundamentals/security/message-security#local-dispatch</docs>
internal static class LocalDispatchAdmission {
  private static readonly AsyncLocal<bool> _admitted = new();

  /// <summary>True inside a stage the dispatcher runs for a message it handles in this process.</summary>
  public static bool IsAdmitted => _admitted.Value;

  /// <summary>Marks the current flow. Call it inside the async method whose flow it should cover.</summary>
  public static void Mark() => _admitted.Value = true;
}
