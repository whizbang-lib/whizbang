// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;

namespace Whizbang.Data.Postgres.Perspectives;

/// <summary>
/// The predicates that find, in one statement and without reading a document into the application, the rows whose
/// stored keys differ from their declared spelling and the rows missing a required member (#1874).
/// </summary>
/// <remarks>
/// <para>
/// Both are <c>jsonb_path_exists</c> over a path the shape describes, one per object it describes, with
/// <c>[*]</c> for every element of a collection. A key in another case is found by matching every key of the object
/// against the declared names case-insensitively and excluding the exact ones. The comparison in PostgreSQL folds
/// case by the database's rules and the rewrite by ordinal rules; for any name outside ASCII where the two disagree, a
/// row the predicate finds and the rewrite leaves alone is simply not updated.
/// </para>
/// <para>
/// Every call passes <c>silent</c>, so a value of an unexpected type at a path (a number where an object is declared)
/// matches nothing rather than failing the statement.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/stored-form-migrations#declared-names</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DeclaredNameSpellingIntegrationTests.cs</tests>
internal static class DeclaredKeySpellingSql {
  /// <summary>Every object the shape describes: its jsonpath, its path as a message names it, and its shape.</summary>
  internal static IEnumerable<(string JsonPath, string Path, DeclaredKeyShape Shape)> Objects(DeclaredKeyShape shape) {
    var pending = new Stack<(string JsonPath, string Path, DeclaredKeyShape Shape)>();
    pending.Push(("$", "$", shape));
    while (pending.Count > 0) {
      var (jsonPath, path, node) = pending.Pop();
      if (node.Element is { } element) {
        pending.Push((jsonPath + "[*]", path + "[*]", element));
        continue;
      }
      yield return (jsonPath, path, node);
      foreach (var member in node.Members.Where(m => m.Shape is not null).Reverse()) {
        pending.Push(($"{jsonPath}.{_string(member.Name)}", $"{path}.{member.Name}", member.Shape!));
      }
    }
  }

  /// <summary>Whether a row's <paramref name="column"/> holds a key in another case than its declared name; <c>false</c> when nothing is declared.</summary>
  internal static string CandidatePredicate(string column, DeclaredKeyShape shape) {
    var quoted = SqlText.Identifier(column);
    var predicates = Objects(shape)
      .Where(o => o.Shape.Members.Count > 0)
      .Select(o => _exists(quoted, $"{o.JsonPath} ? (@.type() == \"object\").keyvalue() ? ({_inAnotherCase(o.Shape)})"))
      .ToList();
    return predicates.Count == 0 ? "false" : string.Join("\n    OR ", predicates);
  }

  /// <summary>One predicate per required member: whether a row's <paramref name="column"/> holds an object at its place without it.</summary>
  internal static IReadOnlyList<(string Path, string Predicate)> MissingRequired(string column, DeclaredKeyShape shape) {
    var quoted = SqlText.Identifier(column);
    return [.. Objects(shape).SelectMany(o => o.Shape.Members
      .Where(m => m.IsRequired)
      .Select(m => ($"{o.Path}.{m.Name}",
        _exists(quoted, $"{o.JsonPath} ? (@.type() == \"object\" && !exists(@.{_string(m.Name)}))"))))];
  }

  private static string _exists(string column, string jsonPath) =>
    $"jsonb_path_exists({column}, {SqlText.Literal(jsonPath)}, '{{}}', true)";

  // A key matching a declared name ignoring case, and none of them exactly.
  private static string _inAnotherCase(DeclaredKeyShape shape) {
    var names = shape.Members.Select(m => m.Name).Distinct(StringComparer.Ordinal).ToList();
    var alternatives = string.Join("|", names.Select(_regexLiteral));
    var exact = string.Concat(names.Select(n => $" && @.key != {_string(n)}"));
    return $"@.key like_regex {_string($"^({alternatives})$")} flag \"i\"{exact}";
  }

  // A name as a jsonpath string literal.
  private static string _string(string value) =>
    "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

  // A name matched literally by a regular expression: every character that is not a letter or a digit is escaped.
  private static string _regexLiteral(string name) {
    var sb = new StringBuilder(name.Length);
    foreach (var c in name) {
      if (!char.IsLetterOrDigit(c)) {
        sb.Append('\\');
      }
      sb.Append(c);
    }
    return sb.ToString();
  }
}
