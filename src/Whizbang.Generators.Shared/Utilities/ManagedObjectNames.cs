// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>
/// Reads, from a perspective table's generated DDL, the names of the indexes and constraints it creates: the
/// objects the generator declares to the managed-object ledger.
/// </summary>
/// <remarks>
/// Read from the very statements the schema pass runs, rather than re-derived from the model, so the declaration
/// cannot drift from what is built: an index the DDL creates is declared, and one it only drops is not. Names are
/// returned unquoted, as PostgreSQL stores them.
/// </remarks>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
/// <tests>tests/Whizbang.Generators.Tests/ManagedObjectNamesTests.cs</tests>
public static class ManagedObjectNames {
  private static readonly Regex _createIndex = new(
    @"\bCREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:CONCURRENTLY\s+)?(?:IF\s+NOT\s+EXISTS\s+)?(""[^""]+""|[A-Za-z_][A-Za-z0-9_$]*)",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, System.TimeSpan.FromSeconds(1));

  private static readonly Regex _addConstraint = new(
    @"\bADD\s+CONSTRAINT\s+(""[^""]+""|[A-Za-z_][A-Za-z0-9_$]*)",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, System.TimeSpan.FromSeconds(1));

  /// <summary>The (kind, name) pairs the DDL creates, in order of first appearance, each once.</summary>
  public static List<(string Kind, string Name)> Extract(string sql) {
    var found = new List<(string Kind, string Name)>();
    var seen = new HashSet<(string, string)>();
    foreach (Match match in _createIndex.Matches(sql)) {
      _add(found, seen, "index", match.Groups[1].Value);
    }
    foreach (Match match in _addConstraint.Matches(sql)) {
      _add(found, seen, "constraint", match.Groups[1].Value);
    }
    return found;
  }

  private static void _add(List<(string, string)> found, HashSet<(string, string)> seen, string kind, string name) {
    var bare = name.Length > 1 && name[0] == '"' ? name.Substring(1, name.Length - 2) : name;
    if (seen.Add((kind, bare))) {
      found.Add((kind, bare));
    }
  }
}
