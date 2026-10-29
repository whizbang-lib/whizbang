// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.ComponentModel;

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// The event types declared <see cref="Attributes.TerminalEventAttribute">terminal</see>, registered by every assembly's
/// generated message catalog when the assembly loads. The flag derivation reads them here instead of through reflection,
/// so a terminal event is stamped <see cref="Messaging.EventFlags.Terminal"/> the same way under native AOT.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#terminal-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/TerminalEventsTests.cs</tests>
public static class TerminalEvents {
  private static readonly ConcurrentDictionary<Type, bool> _types = new();

  /// <summary>
  /// Registers a terminal event type. Called by generated code; registering a type twice is harmless.
  /// </summary>
  /// <param name="eventType">The event type.</param>
  [EditorBrowsable(EditorBrowsableState.Never)]
  public static void Register(Type eventType) {
    ArgumentNullException.ThrowIfNull(eventType);
    _types.TryAdd(eventType, true);
  }

  /// <summary><c>true</c> when <paramref name="eventType"/> was registered as terminal.</summary>
  /// <param name="eventType">The event type.</param>
  /// <returns>Whether storing an event of the type ends its stream.</returns>
  public static bool IsTerminal(Type eventType) {
    ArgumentNullException.ThrowIfNull(eventType);
    return _types.ContainsKey(eventType);
  }
}
