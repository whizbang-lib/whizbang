// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>Which events an upcaster re-keyed on read (#1798).</summary>
public static class EventRekeys {
  /// <summary>The stream an upcaster moved <paramref name="event"/> to, or null when none did.</summary>
  public static Guid? RekeyedTo(IEvent @event) => null;
}
