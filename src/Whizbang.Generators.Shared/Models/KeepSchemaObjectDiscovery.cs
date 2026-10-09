// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Shared.Models;

/// <summary>One object a model pins with <c>[KeepSchemaObject]</c>.</summary>
/// <param name="Name">The object's name.</param>
/// <param name="Reason">Why it is kept, or null.</param>
/// <docs>fundamentals/perspectives/managed-schema-objects#setting-a-code-pin</docs>
public sealed record KeptSchemaObjectInfo(string Name, string? Reason);

/// <summary>Reads <c>[KeepSchemaObject]</c> from a perspective model.</summary>
/// <docs>fundamentals/perspectives/managed-schema-objects#setting-a-code-pin</docs>
/// <tests>tests/Whizbang.Generators.Tests/KeepSchemaObjectDiscoveryTests.cs</tests>
public static class KeepSchemaObjectDiscovery {
  /// <summary>The attribute's full name.</summary>
  public const string ATTRIBUTE = "Whizbang.Core.Perspectives.KeepSchemaObjectAttribute";

  /// <summary>The objects <paramref name="model"/> pins, in declaration order; empty when it pins none.</summary>
  public static ImmutableArray<KeptSchemaObjectInfo> From(INamedTypeSymbol? model) {
    if (model is null) {
      return [];
    }
    return [.. model.GetAttributes()
      .Where(a => TypeNameUtilities.IsNamed(a.AttributeClass, ATTRIBUTE)
        && a.ConstructorArguments.Length == 1
        && a.ConstructorArguments[0].Value is string name
        && !string.IsNullOrWhiteSpace(name))
      .Select(a => new KeptSchemaObjectInfo(
        (string)a.ConstructorArguments[0].Value!,
        a.NamedArguments.FirstOrDefault(n => n.Key == "Reason").Value.Value as string))];
  }
}
