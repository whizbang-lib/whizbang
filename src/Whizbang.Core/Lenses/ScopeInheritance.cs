// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Lenses;

/// <summary>
/// The scope a perspective write carries, and whether it replaces the row's stored scope.
/// </summary>
/// <param name="Scope">The scope to write, or null when the applied messages carried none.</param>
/// <param name="ForceUpdate">
/// True when the store must write <paramref name="Scope"/> over an existing row's scope; false when the
/// scope applies only if the write creates the row.
/// </param>
/// <docs>fundamentals/security/scoping#scope-inheritance</docs>
/// <tests>tests/Whizbang.Core.Tests/Scoping/ScopeInheritanceTests.cs</tests>
public readonly record struct ScopeWrite(PerspectiveScope? Scope, bool ForceUpdate);

/// <summary>
/// Decides the scope a perspective row is written with, from the model's <see cref="InheritScopeAttribute"/>,
/// the latest applied message's scope and the scope the row already has. The generated perspective runner
/// calls it for every write, so the rules live here once.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A new row takes the <see cref="InheritScopeAttribute.OnCreate"/> and
/// <see cref="InheritScopeAttribute.Always"/> fields from the message that created it.</item>
/// <item>An existing row keeps its scope, except the <see cref="InheritScopeAttribute.Always"/> fields,
/// which follow the latest writer (<see cref="PerspectiveScope.WithFieldsFrom"/>).</item>
/// <item>An <see cref="IScopeEvent"/> replaces the scope, as it always has.</item>
/// </list>
/// </remarks>
/// <docs>fundamentals/security/scoping#scope-inheritance</docs>
/// <tests>tests/Whizbang.Core.Tests/Scoping/ScopeInheritanceTests.cs</tests>
public static class ScopeInheritance {
  /// <summary>Decides the scope a perspective write carries.</summary>
  /// <param name="messageScope">The scope of the latest message applied in this write, or null.</param>
  /// <param name="rowScope">The scope the row has now, as the apply read returned it; null for a new row or a row with none.</param>
  /// <param name="rowExists">Whether the row existed when the apply read it.</param>
  /// <param name="onCreate">The model's <see cref="InheritScopeAttribute.OnCreate"/> fields.</param>
  /// <param name="always">The model's <see cref="InheritScopeAttribute.Always"/> fields.</param>
  /// <param name="scopeEventChanged">Whether an <see cref="IScopeEvent"/> set the scope during this apply.</param>
  /// <returns>The scope to write and whether it replaces an existing row's scope.</returns>
  public static ScopeWrite Resolve(
      PerspectiveScope? messageScope,
      PerspectiveScope? rowScope,
      bool rowExists,
      ScopeFields onCreate,
      ScopeFields always,
      bool scopeEventChanged) {
    if (messageScope is null) {
      return new ScopeWrite(null, false);
    }
    if (scopeEventChanged) {
      return new ScopeWrite(messageScope.FilterByFields(onCreate), true);
    }
    if (!rowExists) {
      return new ScopeWrite(messageScope.FilterByFields(onCreate | always), false);
    }
    if (always == ScopeFields.None) {
      return new ScopeWrite(messageScope.FilterByFields(onCreate), false);
    }
    return new ScopeWrite((rowScope ?? new PerspectiveScope()).WithFieldsFrom(messageScope, always), true);
  }
}
