// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// What the stored-vector discovery found: each event type's vector paths, and each vector an event reaches that no
/// path can name.
/// </summary>
/// <param name="Paths">Per event type (fully qualified), its vector paths as JSON-name segments, in the order the generated metadata lists the properties.</param>
/// <param name="Unpacked">The vectors that stay JSON arrays: the event's simple name, the dotted path to the member, and why.</param>
internal sealed record StoredVectorDiscovery(
    IReadOnlyList<(string EventType, IReadOnlyList<IReadOnlyList<string>> Paths)> Paths,
    IReadOnlyList<(string EventName, string Member, string Reason)> Unpacked);

/// <summary>
/// The packed binary32 stored form of vector properties (#2624, V1 of the vector storage plan): which properties are
/// vectors, the converter the generated reader attaches to each, and the per-event registry of their paths.
/// </summary>
/// <docs>data/event-store#packed-vectors</docs>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextStoredVectorTests.cs</tests>
public partial class MessageJsonContextGenerator {
  private const string STORED_AS_JSON_ARRAY_ATTRIBUTE = "Whizbang.Core.Attributes.StoredAsJsonArrayAttribute";
  private const string VECTOR_FIELD_ATTRIBUTE = "Whizbang.Core.Perspectives.VectorFieldAttribute";
  private const string PLACEHOLDER_VECTOR_FACTORY = "__VECTOR_FACTORY__";
  private const string PLACEHOLDER_VECTOR_TYPE = "__VECTOR_TYPE__";
  private const string PLACEHOLDER_VECTOR_PATHS = "__VECTOR_PATHS__";
  private const string REASON_COLLECTION = "it is inside a collection element, and collections of objects are not packed";
  private const string REASON_POLYMORPHIC = "it is behind a polymorphic member, whose path depends on the derived type";
  private const string REASON_CYCLE = "its type refers back to itself, so the path has no fixed depth";

  /// <summary>
  /// Which vector type <paramref name="property"/> is, unless it is marked <c>[StoredAsJsonArray]</c>: a
  /// <c>float[]</c> or an <c>IReadOnlyList&lt;float&gt;</c> always, and a <c>List&lt;float&gt;</c> or a
  /// <c>ReadOnlyMemory&lt;float&gt;</c> only when it is marked <c>[VectorField]</c> (owner decision on #2624: an
  /// unmarked list or memory of floats is an ordinary collection). Every other type, <c>double[]</c> and
  /// <c>float?[]</c> among them, is not: its values do not fit binary32 exactly, or it is not one of the vector types.
  /// </summary>
  private static StoredVectorForm _storedVectorForm(IPropertySymbol property) {
    var attributes = property.GetAttributes();
    if (attributes.Any(static a => TypeNameUtilities.IsNamed(a.AttributeClass, STORED_AS_JSON_ARRAY_ATTRIBUTE))) {
      return StoredVectorForm.None;
    }
    var marked = attributes.Any(static a => TypeNameUtilities.IsNamed(a.AttributeClass, VECTOR_FIELD_ATTRIBUTE));
    return property.Type switch {
      IArrayTypeSymbol { IsSZArray: true, ElementType.SpecialType: SpecialType.System_Single } => StoredVectorForm.Array,
      INamedTypeSymbol named when _isOfFloat(named, "System.Collections.Generic.IReadOnlyList<T>") => StoredVectorForm.ReadOnlyList,
      INamedTypeSymbol named when marked && _isOfFloat(named, "System.Collections.Generic.List<T>") => StoredVectorForm.List,
      INamedTypeSymbol named when marked && _isOfFloat(named, "System.ReadOnlyMemory<T>") => StoredVectorForm.ReadOnlyMemory,
      _ => StoredVectorForm.None,
    };
  }

  private static bool _isOfFloat(INamedTypeSymbol type, string definition) =>
    type.IsGenericType
    && type.TypeArguments[0].SpecialType == SpecialType.System_Single
    && TypeNameUtilities.IsNamed(type.OriginalDefinition, definition);

  /// <summary>
  /// The statement that attaches the stored-vector converter to the property at <paramref name="index"/>, or
  /// <see langword="null"/> when the property is not a vector. The converter reads the packed form or the array and
  /// writes the array through the property's own metadata, so the written bytes do not change.
  /// </summary>
  private static string? _storedVectorConverterStatement(string snippet, PropertyInfo prop, int index) {
    var (factory, vectorType) = prop.VectorForm switch {
      StoredVectorForm.Array => ("ForArray", "float[]"),
      StoredVectorForm.ReadOnlyList => ("ForReadOnlyList", "global::System.Collections.Generic.IReadOnlyList<float>"),
      StoredVectorForm.List => ("ForList", "global::System.Collections.Generic.List<float>"),
      // The serializer has no generated metadata for ReadOnlyMemory<float>: the converter reads and writes it as float[].
      StoredVectorForm.ReadOnlyMemory => ("ForReadOnlyMemory", "float[]"),
      _ => (null, null),
    };
    if (factory is null) {
      return null;
    }
    return snippet
        .Replace(PLACEHOLDER_INDEX, index.ToString(CultureInfo.InvariantCulture))
        .Replace(PLACEHOLDER_VECTOR_FACTORY, factory)
        .Replace(PLACEHOLDER_JSON_PROPERTY_NAME, _stringLiteralContent(prop.WireName))
        .Replace(PLACEHOLDER_VECTOR_TYPE, vectorType);
  }

  /// <summary>
  /// Walks each event type's properties, by JSON name, through the nested object types this context generates, and
  /// collects the path of every vector it reaches. The walk follows the same discovered types the converter is
  /// attached to, so every path names a property whose reader accepts the packed form. A member whose vectors no fixed
  /// path can name (a collection element, a polymorphic member, a type repeating on its own path) is recorded instead.
  /// </summary>
  private static StoredVectorDiscovery _discoverStoredVectorPaths(
      ImmutableArray<JsonMessageTypeInfo> messages,
      ImmutableArray<JsonMessageTypeInfo> allTypes,
      ImmutableArray<PolymorphicTypeInfo> polymorphicTypes) {
    var byName = new Dictionary<string, JsonMessageTypeInfo>(StringComparer.Ordinal);
    foreach (var type in allTypes) {
      byName[type.FullyQualifiedName] = type;
    }
    var derivedByBase = polymorphicTypes.ToDictionary(static p => p.BaseTypeName, static p => p.DerivedTypes, StringComparer.Ordinal);
    var walk = new StoredVectorWalk(byName, derivedByBase);

    var paths = new List<(string, IReadOnlyList<IReadOnlyList<string>>)>();
    foreach (var message in messages.Where(static m => m.IsEvent)) {
      var found = new List<IReadOnlyList<string>>();
      walk.Collect(message, [], [message.FullyQualifiedName], found, message.SimpleName);
      if (found.Count > 0) {
        paths.Add((message.FullyQualifiedName, found));
      }
    }
    return new StoredVectorDiscovery(paths, walk.Unpacked);
  }

  /// <summary>The depth-first walk behind <see cref="_discoverStoredVectorPaths"/>.</summary>
  private sealed class StoredVectorWalk(
      Dictionary<string, JsonMessageTypeInfo> byName,
      Dictionary<string, EquatableArray<string>> derivedByBase) {
    public List<(string EventName, string Member, string Reason)> Unpacked { get; } = [];

    public void Collect(
        JsonMessageTypeInfo type, List<string> prefix, HashSet<string> onPath,
        List<IReadOnlyList<string>> found, string eventName) {
      foreach (var property in type.Properties) {
        var path = new List<string>(prefix) { property.WireName };
        if (property.VectorForm != StoredVectorForm.None) {
          found.Add(path);
          continue;
        }
        var reason = _unpackedReason(property, onPath, out var nested);
        if (reason is not null) {
          Unpacked.Add((eventName, string.Join(".", path), reason));
        } else if (nested is not null) {
          onPath.Add(nested.FullyQualifiedName);
          Collect(nested, path, onPath, found, eventName);
          onPath.Remove(nested.FullyQualifiedName);
        }
      }
    }

    /// <summary>
    /// Why the vectors <paramref name="property"/> reaches have no path, or <see langword="null"/> when they have one
    /// (<paramref name="nested"/> is then the object type to walk into, if the property is one) or there are none.
    /// </summary>
    private string? _unpackedReason(PropertyInfo property, HashSet<string> onPath, out JsonMessageTypeInfo? nested) {
      nested = null;
      var own = property.Type.TrimEnd('?');
      if (derivedByBase.ContainsKey(own)) {
        return _reaches(own) ? REASON_POLYMORPHIC : null;
      }
      var element = _extractElementType(property.Type);
      if (element is not null) {
        return _reaches(element.TrimEnd('?')) ? REASON_COLLECTION : null;
      }
      if (!byName.TryGetValue(own, out var objectType)) {
        return null;
      }
      if (onPath.Contains(own)) {
        return _reaches(own) ? REASON_CYCLE : null;
      }
      nested = objectType;
      return null;
    }

    /// <summary>Whether the type named <paramref name="name"/>, or any derived type when it is a polymorphic base, holds a vector.</summary>
    private bool _reaches(string name) => _reaches(name, new HashSet<string>(StringComparer.Ordinal));

    private bool _reaches(string name, HashSet<string> visited) =>
      derivedByBase.TryGetValue(name, out var derived)
        ? derived.Any(d => _holds(d, visited))
        : _holds(name, visited);

    /// <summary>
    /// Whether a generated type named <paramref name="name"/> holds a vector anywhere beneath it. A type already
    /// visited in this query adds nothing it does not already hold, which ends the search at a cycle.
    /// </summary>
    private bool _holds(string name, HashSet<string> visited) =>
      visited.Add(name)
      && byName.TryGetValue(name, out var type)
      && type.Properties.Any(p =>
        p.VectorForm != StoredVectorForm.None
        || _reaches(_extractElementType(p.Type)?.TrimEnd('?') ?? p.Type.TrimEnd('?'), visited));
  }

  /// <summary>Reports, at Info (WHIZ064), each vector an event reaches that stays a JSON array in the stored body.</summary>
  private static void _reportUnpackedVectors(SourceProductionContext context, StoredVectorDiscovery discovery) {
    foreach (var (eventName, member, reason) in discovery.Unpacked) {
      context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.VectorStoredAsJsonArray, Location.None, member, eventName, reason));
    }
  }

  /// <summary>The <c>StoredVectorPaths.Register</c> calls for the module initializer, one per event type with a vector.</summary>
  private static string _generateStoredVectorPathRegistrations(System.Reflection.Assembly assembly, StoredVectorDiscovery discovery) {
    var snippet = TemplateUtilities.ExtractSnippet(assembly, TEMPLATE_SNIPPET_FILE, "STORED_VECTOR_PATHS_REGISTRATION");
    var sb = new StringBuilder();
    foreach (var (eventType, paths) in discovery.Paths) {
      var literal = string.Join(", ", paths.Select(static path =>
        "[" + string.Join(", ", path.Select(static segment => $"\"{_stringLiteralContent(segment)}\"")) + "]"));
      sb.AppendLine(snippet
          .Replace(PLACEHOLDER_MESSAGE_TYPE, eventType)
          .Replace(PLACEHOLDER_VECTOR_PATHS, literal));
    }
    return sb.ToString();
  }
}
