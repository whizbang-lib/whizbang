// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// The generator's copy of <c>Whizbang.Core.Perspectives.PerspectiveRowColumns</c>: the names a
/// <c>[PerspectiveIndex]</c> declaration uses for a row's own columns, and the column each stands for.
/// </summary>
/// <remarks>
/// A generator cannot reference the core assembly, so the values are repeated here, and a test fails when the two
/// differ. Resolved by <see cref="JsonIndexDiscovery"/> before the model's properties are looked at; a name begins
/// with <c>$</c>, so it can never also be a property's.
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-indexes#the-updated_at-index</docs>
/// <tests>tests/Whizbang.Generators.Tests/UpdatedAtIndexGenerationTests.cs:TheWellKnownNames_AreTheOnesTheGeneratorsResolveAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/UpdatedAtIndexGenerationTests.cs:Discovery_ResolvesEachWellKnownNameToItsColumnAsync</tests>
public static class PerspectiveRowColumnNames {
  /// <summary>The name of the row's <c>updated_at</c> column.</summary>
  public const string UPDATED_AT = "$updated_at";

  /// <summary>The name of the row's <c>created_at</c> column.</summary>
  public const string CREATED_AT = "$created_at";

  /// <summary>The column a well-known name stands for, or null when <paramref name="name"/> is not one.</summary>
  /// <param name="name">A name from a declaration.</param>
  /// <returns>The bare column name, or null.</returns>
  public static string? ColumnFor(string name) => name switch {
    UPDATED_AT => "updated_at",
    CREATED_AT => "created_at",
    _ => null,
  };
}
