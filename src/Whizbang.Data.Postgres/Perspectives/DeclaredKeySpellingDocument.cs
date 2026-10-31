// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json.Nodes;

namespace Whizbang.Data.Postgres.Perspectives;

/// <summary>A key moved to its declared name.</summary>
/// <param name="Path">Where, with <c>[*]</c> for every element of a collection: <c>$.Objects[*].Points[*]</c>.</param>
/// <param name="From">The key as stored.</param>
/// <param name="To">The declared name.</param>
internal sealed record DeclaredKeyRename(string Path, string From, string To);

/// <summary>A key that cannot be moved to its declared name.</summary>
/// <param name="Path">The object holding it, with element indexes: <c>$.Objects[0].Points[1]</c>.</param>
/// <param name="Stored">The key as stored.</param>
/// <param name="Declared">The declared name it would move to, or the names it matches, joined by <c>or</c>.</param>
/// <param name="Ambiguous">Whether the key matches more than one declared name rather than colliding with one.</param>
internal sealed record DeclaredKeyConflict(string Path, string Stored, string Declared, bool Ambiguous = false);

/// <summary>What the rewrite of one document did.</summary>
/// <param name="Json">The rewritten document, or <see langword="null"/> when nothing changed or a conflict stopped it.</param>
/// <param name="Renames">Each key moved.</param>
/// <param name="Conflicts">Each key that could not be moved; any one leaves the whole document as it was.</param>
internal sealed record DeclaredKeyDocumentRewrite(
    string? Json, IReadOnlyList<DeclaredKeyRename> Renames, IReadOnlyList<DeclaredKeyConflict> Conflicts);

/// <summary>
/// Rewrites one stored document to its declared spelling (#1874): every key that matches a declared name only
/// case-insensitively moves to the declared name, at any depth the shape describes and in every element of a
/// collection.
/// </summary>
/// <remarks>
/// <para>
/// A key that is already declared is left alone, so two declared names differing only in case are each kept. A key
/// that matches no declared name is someone else's and is left alone too. Both spellings holding the same value are
/// one value, and the old key is dropped. Both holding different values, or a key that matches two declared names,
/// cannot be decided by case alone: that is a conflict, and a document with any conflict is not rewritten at all.
/// </para>
/// <para>
/// Values move as parsed, so a number keeps the exact text it was stored with.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/stored-form-migrations#declared-names</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DeclaredKeySpellingDocumentTests.cs</tests>
internal static class DeclaredKeySpellingDocument {
  private const string ROOT = "$";
  private const string EVERY = "[*]";

  /// <summary>Rewrites <paramref name="json"/> against <paramref name="shape"/>.</summary>
  /// <param name="json">The stored document.</param>
  /// <param name="shape">What its reader declares.</param>
  internal static DeclaredKeyDocumentRewrite Rewrite(string json, DeclaredKeyShape shape) {
    ArgumentNullException.ThrowIfNull(json);
    ArgumentNullException.ThrowIfNull(shape);

    var root = JsonNode.Parse(json);
    var walk = new Walk();
    walk.Visit(root, shape, ROOT, ROOT);

    if (walk.Conflicts.Count > 0) {
      return new DeclaredKeyDocumentRewrite(null, [], walk.Conflicts);
    }
    return walk.Renames.Count == 0
      ? new DeclaredKeyDocumentRewrite(null, [], [])
      : new DeclaredKeyDocumentRewrite(root!.ToJsonString(), walk.Renames, []);
  }

  private sealed class Walk {
    public List<DeclaredKeyRename> Renames { get; } = [];

    public List<DeclaredKeyConflict> Conflicts { get; } = [];

    public void Visit(JsonNode? node, DeclaredKeyShape shape, string shapePath, string path) {
      if (shape.Element is { } element) {
        if (node is JsonArray array) {
          for (var i = 0; i < array.Count; i++) {
            Visit(array[i], element, shapePath + EVERY, string.Create(CultureInfo.InvariantCulture, $"{path}[{i}]"));
          }
        }
        return;
      }
      if (node is JsonObject obj) {
        _visitObject(obj, shape, shapePath, path);
      }
    }

    private void _visitObject(JsonObject obj, DeclaredKeyShape shape, string shapePath, string path) {
      foreach (var key in obj.Select(p => p.Key).ToList()) {
        _respell(obj, key, shape, shapePath, path);
      }
      foreach (var member in shape.Members) {
        if (member.Shape is { } nested && obj.TryGetPropertyValue(member.Name, out var child)) {
          Visit(child, nested, $"{shapePath}.{member.Name}", $"{path}.{member.Name}");
        }
      }
    }

    private void _respell(JsonObject obj, string key, DeclaredKeyShape shape, string shapePath, string path) {
      if (shape.Declares(key)) {
        return;
      }
      var matches = shape.CaseVariantsOf(key);
      if (matches.Count == 0) {
        return;
      }
      if (matches.Count > 1) {
        Conflicts.Add(new DeclaredKeyConflict(path, key, string.Join(" or ", matches), Ambiguous: true));
        return;
      }

      var declared = matches[0];
      var value = obj[key];
      if (obj.TryGetPropertyValue(declared, out var existing)) {
        if (!JsonNode.DeepEquals(existing, value)) {
          Conflicts.Add(new DeclaredKeyConflict(path, key, declared));
          return;
        }
        obj.Remove(key);
      } else {
        obj.Remove(key);
        obj[declared] = value;
      }
      Renames.Add(new DeclaredKeyRename(shapePath, key, declared));
    }
  }
}
