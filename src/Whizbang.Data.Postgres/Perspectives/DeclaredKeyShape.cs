// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.Data.Postgres.Perspectives;

/// <summary>One key a reader reads in a stored object, and what it reads under it.</summary>
/// <param name="Name">The declared name: the key as the reader matches it.</param>
/// <param name="IsRequired">Whether the reader refuses a document missing the key.</param>
/// <param name="Shape">What is read under the key, or <see langword="null"/> for a value with no keys of its own.</param>
/// <docs>fundamentals/perspectives/stored-form-migrations#declared-names</docs>
public sealed record DeclaredMember(string Name, bool IsRequired = false, DeclaredKeyShape? Shape = null);

/// <summary>
/// The keys a reader reads at one place in a stored document: an object's declared members, or the elements of a
/// collection (#1874).
/// </summary>
/// <remarks>
/// <para>
/// A property is stored under its declared <c>[JsonPropertyName]</c>, and a release that did not honor the attribute
/// stored it under its C# name. Where the two differ only in case, a reader that matches names exactly finds no key
/// and leaves the member at its default without an error. The shape is what the rewrite compares stored keys
/// against, so it has to come from the thing that reads the document: Entity Framework's model for a document it
/// maps, and the serializer's metadata otherwise. Neither is reflection.
/// </para>
/// <para>
/// A dictionary is not described: its keys are data, not declared names. A type that contains itself is described
/// down to the point where it recurs.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/stored-form-migrations#declared-names</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DeclaredKeySpellingDocumentTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/DeclaredKeyShapesTests.cs</tests>
public sealed class DeclaredKeyShape {
  private readonly HashSet<string> _declared;
  private readonly Dictionary<string, string[]> _byFoldedName;

  private DeclaredKeyShape(IReadOnlyList<DeclaredMember> members, DeclaredKeyShape? element) {
    Members = members;
    Element = element;
    _declared = members.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
    _byFoldedName = members.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(g => g.Key, g => g.Select(m => m.Name).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.OrdinalIgnoreCase);
  }

  /// <summary>The declared members of an object; empty for a collection.</summary>
  public IReadOnlyList<DeclaredMember> Members { get; }

  /// <summary>What each element of a collection is read as; <see langword="null"/> for an object.</summary>
  public DeclaredKeyShape? Element { get; }

  /// <summary>An object with these declared members.</summary>
  /// <param name="members">The members, in the order the reader declares them.</param>
  public static DeclaredKeyShape ForObject(params DeclaredMember[] members) => ForObject((IEnumerable<DeclaredMember>)members);

  /// <summary>An object with these declared members.</summary>
  /// <param name="members">The members, in the order the reader declares them.</param>
  public static DeclaredKeyShape ForObject(IEnumerable<DeclaredMember> members) {
    ArgumentNullException.ThrowIfNull(members);
    return new DeclaredKeyShape([.. members], null);
  }

  /// <summary>A collection whose every element is read as <paramref name="element"/>.</summary>
  /// <param name="element">The element's shape.</param>
  public static DeclaredKeyShape ForArray(DeclaredKeyShape element) {
    ArgumentNullException.ThrowIfNull(element);
    return new DeclaredKeyShape([], element);
  }

  /// <summary>
  /// The shape the serializer reads <paramref name="type"/> as, from its metadata, or <see langword="null"/> when the
  /// type has no declared keys anywhere (a number, a string, a dictionary, a type nothing describes).
  /// </summary>
  /// <param name="options">The options the document is read with.</param>
  /// <param name="type">The document's type.</param>
  public static DeclaredKeyShape? FromSerializer(JsonSerializerOptions options, Type type) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(type);
    return _fromSerializer(options, type, []);
  }

  /// <summary>Whether <paramref name="key"/> is a declared name, exactly.</summary>
  internal bool Declares(string key) => _declared.Contains(key);

  /// <summary>The declared names <paramref name="key"/> matches ignoring case; empty when it matches none.</summary>
  internal IReadOnlyList<string> CaseVariantsOf(string key) =>
    _byFoldedName.TryGetValue(key, out var names) ? names : [];

  /// <summary>
  /// The shape as stable text: every member quoted, <c>!</c> after a required one, a nested shape after a colon, a
  /// collection in brackets. Equal shapes describe themselves equally, which is what the journal keys on.
  /// </summary>
  public string Describe() {
    var sb = new StringBuilder();
    _describe(sb);
    return sb.ToString();
  }

  private void _describe(StringBuilder sb) {
    if (Element is { } element) {
      sb.Append('[');
      element._describe(sb);
      sb.Append(']');
      return;
    }
    sb.Append('{');
    for (var i = 0; i < Members.Count; i++) {
      if (i > 0) {
        sb.Append(',');
      }
      var member = Members[i];
      sb.Append('"').Append(member.Name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
      if (member.IsRequired) {
        sb.Append('!');
      }
      if (member.Shape is { } nested) {
        sb.Append(':');
        nested._describe(sb);
      }
    }
    sb.Append('}');
  }

  private static DeclaredKeyShape? _fromSerializer(JsonSerializerOptions options, Type type, HashSet<Type> visiting) {
    var underlying = Nullable.GetUnderlyingType(type) ?? type;
    // The resolver rather than the options, because the options throw for a type nothing describes.
    var info = options.TypeInfoResolver?.GetTypeInfo(underlying, options);
    if (info is null || !visiting.Add(underlying)) {
      return null;
    }

    try {
      return info.Kind switch {
        JsonTypeInfoKind.Object => ForObject(info.Properties.Select(p =>
          new DeclaredMember(p.Name, p.IsRequired, _fromSerializer(options, p.PropertyType, visiting)))),
        JsonTypeInfoKind.Enumerable => _arrayOf(_fromSerializer(options, info.ElementType!, visiting)),
        _ => null,
      };
    } finally {
      visiting.Remove(underlying);
    }
  }

  // A collection of values with no keys has nothing to describe.
  private static DeclaredKeyShape? _arrayOf(DeclaredKeyShape? element) => element is null ? null : ForArray(element);
}
