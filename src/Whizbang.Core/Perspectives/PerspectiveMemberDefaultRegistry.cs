// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Process-wide map of a perspective model's members to the value a missing document key reads as, populated from
/// the generated perspective runner's <c>[ModuleInitializer]</c> exactly as
/// <see cref="PerspectivePhysicalFieldRegistry"/> is.
/// </summary>
/// <remarks>
/// <para>
/// A document written before a member existed carries no key for it. Deserialization gives that member its declared
/// default, so the in-memory replay evaluates a predicate against the default; the collective apply path reads the
/// raw document, where the absent key is SQL <c>NULL</c>. Without this map the two disagree, and the rows they
/// disagree about are the oldest ones (#1044).
/// </para>
/// <para>
/// Only a member whose declaration carries a default is registered. A member that would read as <c>null</c> anyway
/// is left out, so a predicate that is correct today keeps its meaning.
/// </para>
/// <para>
/// Self-registration rather than attribute scanning is an AOT requirement, and it is also the only way to see a
/// property initializer at all: <c>= "Draft"</c> is visible to the generator at compile time, while
/// <c>default(T)</c> at run time reports <c>null</c> for it.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/CollectivePredicateDefaultSemanticsTests.cs</tests>
public static class PerspectiveMemberDefaultRegistry {
  private static readonly ConcurrentDictionary<(Type ModelType, string PropertyName), object> _defaults = new();

  /// <summary>
  /// Registers the value an absent document key reads as for one member of a model. Idempotent; the last
  /// registration wins, so two perspectives over one model registering the same members is harmless.
  /// </summary>
  /// <param name="modelType">The perspective model declaring the member.</param>
  /// <param name="propertyName">The member's name.</param>
  /// <param name="defaultValue">
  /// The member's declared default. Passing <see langword="null"/> removes any registration: a member that reads as
  /// null needs no help, and registering one would change a predicate that is already correct.
  /// </param>
  public static void Register(Type modelType, string propertyName, object? defaultValue) {
    ArgumentNullException.ThrowIfNull(modelType);
    ArgumentException.ThrowIfNullOrEmpty(propertyName);

    if (defaultValue is null) {
      _defaults.TryRemove((modelType, propertyName), out _);
      return;
    }

    _defaults[(modelType, propertyName)] = defaultValue;
  }

  /// <summary>
  /// The value an absent document key reads as for one member, when the member declares one.
  /// </summary>
  /// <param name="modelType">The perspective model declaring the member.</param>
  /// <param name="propertyName">The member's name.</param>
  /// <param name="defaultValue">The declared default, when this returns <see langword="true"/>.</param>
  /// <returns><see langword="true"/> when the member declares a default.</returns>
  public static bool TryResolve(Type modelType, string propertyName, out object? defaultValue) {
    ArgumentNullException.ThrowIfNull(modelType);
    ArgumentException.ThrowIfNullOrEmpty(propertyName);

    var found = _defaults.TryGetValue((modelType, propertyName), out var value);
    defaultValue = found ? value : null;
    return found;
  }
}
