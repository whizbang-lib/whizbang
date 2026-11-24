// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Dispatch;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The ambient sequence integrity: the setting every dispatch inside the scope takes when it names none itself. The
/// counterpart of the ambient priority (<see cref="Whizbang.Core.Priority.PriorityContext"/>), and inherited the same
/// way: while the dispatcher handles a message whose own dispatch set a sequence integrity explicitly, it runs the
/// handling inside a scope carrying that setting, so everything the receptor returns or dispatches inherits it.
/// </summary>
/// <remarks>
/// Precedence, most specific first: the dispatch's own <see cref="DispatchOptions.SequenceIntegrity"/>, then this scope
/// (entered by the caller, or by the parent dispatch), then the event type's <c>[SequenceIntegrity]</c>, then
/// <see cref="Configuration.WhizbangCoreOptions.SequenceIntegrity"/>. The hold time resolves the same way, from the most
/// specific level that names one: a scope that names none keeps the enclosing scope's. The setting flows with the async
/// context into the work a handler starts, and disposing a scope restores the one it replaced.
/// </remarks>
/// <docs>fundamentals/messaging/sequence-integrity#ambient-scope</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/SequenceIntegrityContextTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/DispatcherSequenceIntegrityInheritanceTests.cs</tests>
public static class SequenceIntegrityContext {
  private static readonly AsyncLocal<Setting?> _current = new();

  /// <summary>The ambient mode, or null outside any scope.</summary>
  /// <docs>fundamentals/messaging/sequence-integrity#ambient-scope</docs>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/SequenceIntegrityContextTests.cs:Enter_SetsTheModeAndHoldTime_AndDisposingClearsThemAsync</tests>
  public static SequenceIntegrity? CurrentMode => _current.Value?.Mode;

  /// <summary>The ambient hold time, or null when no scope names one.</summary>
  /// <docs>fundamentals/messaging/sequence-integrity#ambient-scope</docs>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/SequenceIntegrityContextTests.cs:NestedScope_WithNoHoldTime_KeepsTheOuterHoldTimeAsync</tests>
  public static TimeSpan? CurrentHoldTime => _current.Value?.HoldTime;

  /// <summary>
  /// Enters a scope in which every dispatch that names no sequence integrity itself takes <paramref name="mode"/>.
  /// Disposing it restores the previous setting.
  /// </summary>
  /// <param name="mode">The sequence integrity for the dispatches inside the scope.</param>
  /// <param name="holdTime">For a bounded hold, how long to hold at most; null keeps the enclosing scope's, else the
  /// type's or the configured one.</param>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="holdTime"/> is negative.</exception>
  /// <docs>fundamentals/messaging/sequence-integrity#ambient-scope</docs>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/SequenceIntegrityContextTests.cs:NestedScope_ReplacesTheOuterOne_AndDisposingRestoresItAsync</tests>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/SequenceIntegrityContextTests.cs:Enter_ANegativeHoldTime_IsRefusedAsync</tests>
  public static IDisposable Enter(SequenceIntegrity mode, TimeSpan? holdTime = null) {
    if (holdTime is { } time && time < TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(nameof(holdTime), time, "A hold time cannot be negative.");
    }
    var previous = _current.Value;
    _current.Value = new Setting(mode, holdTime ?? previous?.HoldTime);
    return new Scope(previous);
  }

  /// <summary>
  /// The scope a dispatch runs its handling in: its own explicit setting, or nothing (null) when it set none, so what
  /// its receptors produce inherits only what was set explicitly.
  /// </summary>
  internal static IDisposable? EnterFor(DispatchOptions? options) =>
    options?.SequenceIntegrity is { } mode ? Enter(mode, options.SequenceHoldTime) : null;

  private sealed record Setting(SequenceIntegrity Mode, TimeSpan? HoldTime);

  private sealed class Scope(Setting? previous) : IDisposable {
    public void Dispose() => _current.Value = previous;
  }
}
