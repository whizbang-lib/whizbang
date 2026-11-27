// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// The packed binary32 stored form of vector properties (#2624, V1 of the vector storage plan): which properties are
/// vectors, and the converter the generated reader attaches to each.
/// </summary>
/// <docs>data/event-store#packed-vectors</docs>
/// <tests>tests/Whizbang.Generators.Tests/MessageJsonContextStoredVectorTests.cs</tests>
public partial class MessageJsonContextGenerator {
  private const string STORED_AS_JSON_ARRAY_ATTRIBUTE = "Whizbang.Core.Attributes.StoredAsJsonArrayAttribute";
  private const string PLACEHOLDER_VECTOR_FACTORY = "__VECTOR_FACTORY__";
  private const string PLACEHOLDER_VECTOR_TYPE = "__VECTOR_TYPE__";

  /// <summary>
  /// Which vector type <paramref name="property"/> is: a <c>float[]</c> or an <c>IReadOnlyList&lt;float&gt;</c> not
  /// marked <c>[StoredAsJsonArray]</c>. Every other type, <c>double[]</c> and <c>float?[]</c> among them, is not: its
  /// values do not fit binary32 exactly, or it is not one of the plan's vector types.
  /// </summary>
  private static StoredVectorForm _storedVectorForm(IPropertySymbol property) {
    if (property.GetAttributes().Any(static a => TypeNameUtilities.IsNamed(a.AttributeClass, STORED_AS_JSON_ARRAY_ATTRIBUTE))) {
      return StoredVectorForm.None;
    }
    return property.Type switch {
      IArrayTypeSymbol { IsSZArray: true, ElementType.SpecialType: SpecialType.System_Single } => StoredVectorForm.Array,
      INamedTypeSymbol named when _isReadOnlyListOfFloat(named) => StoredVectorForm.ReadOnlyList,
      _ => StoredVectorForm.None,
    };
  }

  private static bool _isReadOnlyListOfFloat(INamedTypeSymbol type) =>
    type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IReadOnlyList_T
    && type.TypeArguments[0].SpecialType == SpecialType.System_Single;

  /// <summary>
  /// The statement that attaches the stored-vector converter to the property at <paramref name="index"/>, or
  /// <see langword="null"/> when the property is not a vector. The converter reads the packed form or the array and
  /// writes the array through the property's own metadata, so the written bytes do not change.
  /// </summary>
  private static string? _storedVectorConverterStatement(string snippet, PropertyInfo prop, int index) {
    var (factory, vectorType) = prop.VectorForm switch {
      StoredVectorForm.Array => ("ForArray", "float[]"),
      StoredVectorForm.ReadOnlyList => ("ForReadOnlyList", "global::System.Collections.Generic.IReadOnlyList<float>"),
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
}
