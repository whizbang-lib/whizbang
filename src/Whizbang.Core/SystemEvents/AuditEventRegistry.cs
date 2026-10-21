// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// The <see cref="AuditEventAttribute"/> each event type declares, directly or through a base class, registered at
/// module load by the generator so audit decisions read it without reflection (#1580).
/// </summary>
/// <remarks>
/// A type the generator cannot name from generated code (a private nested type) is reported with WHIZ180 and is not
/// registered; register it here by hand, or make it internal or public.
/// </remarks>
/// <docs>fundamentals/events/system-events#audit-decisions</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditEventRegistryTests.cs</tests>
public static class AuditEventRegistry {
  private static readonly ConcurrentDictionary<Type, AuditEventAttribute> _byType = new();

  /// <summary>Records <paramref name="attribute"/> as the audit settings of <paramref name="eventType"/>.</summary>
  /// <param name="eventType">The event's CLR type.</param>
  /// <param name="attribute">The attribute it declares or inherits.</param>
  public static void Register(Type eventType, AuditEventAttribute attribute) {
    ArgumentNullException.ThrowIfNull(eventType);
    ArgumentNullException.ThrowIfNull(attribute);
    _byType[eventType] = attribute;
  }

  /// <summary>The audit settings registered for <paramref name="eventType"/>, or null when it declares none.</summary>
  /// <param name="eventType">The event's CLR type.</param>
  /// <returns>The attribute, or null.</returns>
  public static AuditEventAttribute? Find(Type eventType) =>
    _byType.TryGetValue(eventType, out var attribute) ? attribute : null;
}
