// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// The properties a perspective model keeps only in its document: every readable instance property, its
/// base types' included, that is not promoted to a column.
/// </summary>
/// <remarks>
/// A demoted field is one of these whose column an earlier release left on the table, so these are the
/// candidates the schema pass hands to <see cref="PhysicalColumnSql.Demote"/> (issue #1022).
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#demoting-a-field</docs>
/// <tests>tests/Whizbang.Generators.Tests/PhysicalFieldMoveGenerationTests.cs</tests>
public static class DocumentPropertyDiscovery {
  private const string PHYSICAL_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.PhysicalFieldAttribute";
  private const string VECTOR_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.VectorFieldAttribute";

  /// <summary>The names of the model's properties kept only in its document, in declaration order.</summary>
  /// <param name="modelType">The perspective's model type.</param>
  /// <returns>Property names, each once.</returns>
  public static ImmutableArray<string> From(INamedTypeSymbol? modelType) {
    var names = new List<string>();
    var seen = new HashSet<string>();
    for (var type = modelType; type is not null && type.SpecialType != SpecialType.System_Object; type = type.BaseType) {
      foreach (var property in type.GetMembers().OfType<IPropertySymbol>()) {
        if (property.IsStatic || property.IsIndexer || property.GetMethod is null
            || property.DeclaredAccessibility != Accessibility.Public || !seen.Add(property.Name)) {
          continue;
        }
        if (!property.GetAttributes().Any(a => TypeNameUtilities.IsNamed(a.AttributeClass, PHYSICAL_FIELD_ATTRIBUTE)
            || TypeNameUtilities.IsNamed(a.AttributeClass, VECTOR_FIELD_ATTRIBUTE))) {
          names.Add(property.Name);
        }
      }
    }
    return [.. names];
  }
}
