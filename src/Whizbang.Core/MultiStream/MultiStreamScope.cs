// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Security;

namespace Whizbang.Core.MultiStream;

/// <summary>
/// Carries the security scope a multi-stream command was sent under to the instances that work its streams later. The
/// scope is kept as the same delta a message hop carries, so it round-trips everything a hop does.
/// </summary>
/// <docs>fundamentals/dispatcher/multi-stream-commands#security-scope</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/MultiStreamCommandIntakeTests.cs:HandleAsync_UnderAnAmbientScope_RecordsThatScopeForTheHandlersAsync</tests>
public static class MultiStreamScope {
  /// <summary>Serializes <paramref name="context"/>, or returns null when there is no ambient scope.</summary>
  /// <param name="context">The ambient scope at intake, or null.</param>
  /// <returns>The scope as stored on a run, or null.</returns>
  public static string? Capture(IScopeContext? context) {
    if (context is null) {
      return null;
    }
    // Measured from no scope at all, so the delta always carries the scope itself, empty or not.
    return MultiStreamJson.Serialize(ScopeDelta.CreateDelta(null, context));
  }

  /// <summary>Reads a stored scope back into a context to run a handler under, or null when the run carried none.</summary>
  /// <param name="scope">The scope as stored on a run.</param>
  /// <returns>The restored scope, or null.</returns>
  public static ScopeContext? Restore(string? scope) {
    if (string.IsNullOrEmpty(scope)) {
      return null;
    }
    return MultiStreamJson.Deserialize<ScopeDelta>(scope)?.ApplyTo(null);
  }
}
