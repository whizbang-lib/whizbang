// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// Names a generator gives PostgreSQL objects, kept within what PostgreSQL will store.
/// </summary>
/// <remarks>
/// <para>
/// PostgreSQL does not refuse an identifier longer than 63 bytes; it truncates it. Two derived names
/// that differ only past that point therefore arrive as one, and <c>CREATE INDEX IF NOT EXISTS</c>
/// quietly skips the second: a long property declared both case-sensitive and case-insensitive got
/// one index, and a long table name made its <c>created_at</c> and <c>updated_at</c> indexes collide.
/// A name that would not fit keeps its first 54 characters and ends in a digest of the whole name, so
/// it stays unique and is the same on every run.
/// </para>
/// <para>
/// A name that already fits is returned unchanged, so no index anyone already has is renamed. For one
/// that did not fit, the new name differs from the truncated one PostgreSQL stored, and the schema
/// pass creates indexes through <c>wh_ensure_index</c>, which finds the existing index by its
/// definition and does not build a second.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-indexes</docs>
/// <tests>tests/Whizbang.Generators.Tests/PostgresIdentifiersTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/CompositeIndexNamingTests.cs</tests>
public static class PostgresIdentifiers {
  /// <summary>PostgreSQL's identifier limit (NAMEDATALEN - 1).</summary>
  public const int LIMIT = 63;

  /// <summary>
  /// <paramref name="name"/> as it is when it fits, or shortened with a stable digest suffix when it
  /// does not.
  /// </summary>
  /// <param name="name">The derived name.</param>
  /// <returns>A name of at most <see cref="LIMIT"/> characters.</returns>
  public static string WithinLimit(string name) {
    if (name.Length <= LIMIT) {
      return name;
    }

    var suffix = "_" + StableHash(name).ToString("x8", CultureInfo.InvariantCulture);

    // A range rather than Substring's two arguments; the project targets the generator's
    // netstandard2.0, where string.Concat has no span overload, so this stays a string.
    return name[..(LIMIT - suffix.Length)] + suffix;
  }

  /// <summary>A hash that is the same on every run.</summary>
  /// <remarks>
  /// FNV-1a rather than <c>string.GetHashCode</c>, which is randomized per process: a generator has
  /// to emit identical output for identical input, or every build looks like a change to the
  /// incremental cache and to anything comparing generated files.
  /// </remarks>
  /// <param name="value">The text to hash.</param>
  /// <returns>The 32-bit FNV-1a hash.</returns>
  public static uint StableHash(string value) {
    var hash = 2166136261u;

    foreach (var c in value) {
      hash = (hash ^ c) * 16777619u;
    }

    return hash;
  }
}
