// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Routing;

/// <summary>
/// The subject (the broker's label, Azure Service Bus <c>Subject</c> / <c>sys.Label</c>) an event is published
/// under: its CLR-form type name, lowercased (<c>myapp.orders.events.outer+orderplaced</c>). The publish path stamps
/// it and the excluded-event-types rule (#2175) filters on it, both through this one rendering, so the two can never
/// disagree on a nested or renamed type.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#excluded-event-types</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/EventSubjectTests.cs</tests>
public static class EventSubject {
  /// <summary>The subject of an event type given by name, in the wire or the CLR form.</summary>
  /// <param name="typeName">The type name (<c>"MyApp.Events.Outer+OrderPlaced, MyApp"</c> or
  /// <c>"MyApp.Events.Outer+OrderPlaced"</c>).</param>
  /// <returns>The lowercased CLR form.</returns>
  /// <exception cref="ArgumentException">Thrown when <paramref name="typeName"/> is null, empty or whitespace.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Routing/EventSubjectTests.cs:For_WireForm_IsTheLowercasedClrFormAsync</tests>
  public static string For(string typeName) {
    throw new NotImplementedException();
  }

  /// <summary>The subject of an event type.</summary>
  /// <param name="eventType">The event type.</param>
  /// <returns>The lowercased CLR form.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Routing/EventSubjectTests.cs:For_Type_IsTheSubjectOfItsWireFormAsync</tests>
  public static string For(Type eventType) {
    throw new NotImplementedException();
  }
}
