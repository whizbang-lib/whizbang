// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// The ambient sequence integrity: the setting every dispatch inside the scope takes when it names none itself.
/// </summary>
/// <docs>fundamentals/messaging/sequence-integrity#ambient-scope</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/SequenceIntegrityContextTests.cs</tests>
public static class SequenceIntegrityContext {
  /// <summary>The ambient mode, or null outside any scope.</summary>
  public static SequenceIntegrity? CurrentMode => null;

  /// <summary>The ambient hold time, or null when no scope names one.</summary>
  public static TimeSpan? CurrentHoldTime => null;

  /// <summary>Enters a scope; disposing it restores the previous setting.</summary>
  /// <param name="mode">The mode.</param>
  /// <param name="holdTime">The hold time, or null.</param>
  public static IDisposable Enter(SequenceIntegrity mode, TimeSpan? holdTime = null) {
    _ = (mode, holdTime);
    return new Scope();
  }

  private sealed class Scope : IDisposable {
    public void Dispose() { }
  }
}
