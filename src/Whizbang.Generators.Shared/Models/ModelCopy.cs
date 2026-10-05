// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// How generated code copies a class model into a new instance: the properties an object initializer carries from
/// the old instance, or why the class cannot be copied that way.
/// </summary>
/// <remarks>This record uses value equality, which the incremental generator's caching depends on.</remarks>
/// <param name="Members">The properties the copy carries, in order, separated by commas.</param>
/// <param name="Problem">Why the class cannot be copied, or <see langword="null"/> when it can.</param>
/// <docs>fundamentals/perspectives/physical-fields#reading-promoted-fields-back</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveRunnerSplitInitOnlyTests.cs</tests>
public sealed record ModelCopyInfo(string Members, string? Problem);

/// <summary>
/// Copies a class model with an object initializer: a new instance from its parameterless constructor, every
/// property the document reads carried over, and the properties a caller names set to the values it gives.
/// </summary>
/// <remarks>
/// <para>
/// A Split model's promoted fields live only in their columns. The runner strips them from the model before the write
/// and copies the columns back into a model it loads. A record does both with a <c>with</c> expression and a settable
/// property of a class is assigned in place, but an <c>init</c>-only property of a class can be set only while an
/// instance is being created, so for that model both go through a copy (issue #1002).
/// </para>
/// <para>
/// The copy carries every public instance property that has a setter the generated code can reach (public, or
/// internal in the same assembly), <c>init</c> included. A get-only property with no backing field is computed and
/// is not carried. A get-only property that stores a value (an auto-property) and one whose setter the generated code
/// cannot reach would be lost, so the class cannot be copied, and neither can one without a reachable parameterless
/// constructor or an abstract one.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#reading-promoted-fields-back</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveRunnerSplitInitOnlyTests.cs</tests>
public static class ModelCopy {
  /// <summary>How a class model is copied by code generated into <paramref name="within"/>.</summary>
  /// <param name="model">The model type.</param>
  /// <param name="within">The assembly the generated code is compiled into.</param>
  public static ModelCopyInfo For(INamedTypeSymbol model, IAssemblySymbol within) {
    if (model.IsAbstract) {
      return new ModelCopyInfo("", "it is abstract");
    }
    if (!model.InstanceConstructors.Any(c => c.Parameters.Length == 0 && _reachable(c, within))) {
      return new ModelCopyInfo("", "it has no public or internal parameterless constructor");
    }

    var members = new List<string>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    for (var type = model; type is { SpecialType: SpecialType.None }; type = type.BaseType) {
      foreach (var property in type.GetMembers().OfType<IPropertySymbol>().Where(p => _isCandidate(p, seen))) {
        if (_blocker(property, type, within) is { } reason) {
          return new ModelCopyInfo("", reason);
        }
        if (property.SetMethod is not null) {
          members.Add(property.Name);
        }
      }
    }
    return new ModelCopyInfo(string.Join(",", members), null);
  }

  /// <summary>A public instance property not yet seen lower in the hierarchy (an override or a hiding member).</summary>
  private static bool _isCandidate(IPropertySymbol property, HashSet<string> seen) =>
    !property.IsStatic && !property.IsIndexer && property.DeclaredAccessibility == Accessibility.Public
    && seen.Add(property.Name);

  /// <summary>Why <paramref name="property"/> stops the copy, or null when it is carried or is computed.</summary>
  private static string? _blocker(IPropertySymbol property, INamedTypeSymbol type, IAssemblySymbol within) {
    if (property.SetMethod is { } setter) {
      return _reachable(setter, within) ? null : $"the setter of {property.Name} is not public or internal";
    }
    return type.GetMembers().OfType<IFieldSymbol>().Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, property))
      ? $"{property.Name} is a get-only property that stores a value, which a copy cannot carry"
      : null;
  }

  /// <summary>
  /// The object initializer that copies <paramref name="source"/> into a new <paramref name="typeName"/>, with each
  /// property in <paramref name="overrides"/> set to its value instead of carried over.
  /// </summary>
  /// <param name="typeName">The model type, as generated code names it.</param>
  /// <param name="copy">How the model is copied.</param>
  /// <param name="source">The expression naming the instance copied from.</param>
  /// <param name="overrides">Each property set to another value, and that value.</param>
  public static string Render(string typeName, ModelCopyInfo copy, string source, IReadOnlyDictionary<string, string> overrides) {
    var assignments = copy.Members.Split([','], StringSplitOptions.RemoveEmptyEntries)
      .Select(m => $"{m} = {(overrides.TryGetValue(m, out var value) ? value : $"{source}.{m}")}");
    return $"new {typeName} {{ {string.Join(", ", assignments)} }}";
  }

  private static bool _reachable(IMethodSymbol method, IAssemblySymbol within) =>
    method.DeclaredAccessibility == Accessibility.Public
    || (method.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal
        && SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, within));
}
