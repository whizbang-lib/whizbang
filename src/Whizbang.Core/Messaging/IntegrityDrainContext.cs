// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Marks the flow of control the integrity delivery drain runs a handler on (#2001). An integrity receptor invoked
/// inside it runs for real instead of recording a delivery: the drain is already on the maintainer, under its
/// admission, so deferring again would only queue the work behind itself.
/// </summary>
/// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityDeliveryDeferralTests.cs:TheDrainContext_EndsWithItsScope_AndNestsAsync</tests>
internal static class IntegrityDrainContext {
  private static readonly AsyncLocal<bool> _active = new();

  /// <summary>Whether the current flow of control is inside the drain.</summary>
  public static bool IsActive => _active.Value;

  /// <summary>Enters the drain until the returned scope is disposed, which restores the previous state.</summary>
  /// <returns>The scope.</returns>
  public static IDisposable Enter() {
    var previous = _active.Value;
    _active.Value = true;
    return new Scope(previous);
  }

  private sealed class Scope(bool previous) : IDisposable {
    public void Dispose() => _active.Value = previous;
  }
}
