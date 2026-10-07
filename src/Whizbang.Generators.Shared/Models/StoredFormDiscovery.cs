// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators.Shared.Models;

/// <summary>What a stored-form migration does to a physical column, besides its document.</summary>
public enum StoredFormColumnAction {
  /// <summary>Nothing: the property is not a physical field, or its column is converted elsewhere.</summary>
  None = 0,

  /// <summary>The column is retyped to the property's current type.</summary>
  Retype = 1,

  /// <summary>The column is renamed from the former property's column.</summary>
  Rename = 2,
}

/// <summary>
/// One stored-form migration a model declares, pre-rendered for emission: its name, the <c>StoredFormStep</c> call
/// that converts the document, and what it does to the property's physical column when it has one.
/// </summary>
/// <remarks>This record uses value equality, which the incremental generator's caching depends on.</remarks>
/// <param name="Order">0 rename, 1 type conversion, 2 default, 3 removal: the order they run in within a table.</param>
/// <param name="NameSuffix">The migration's name after the table: <c>Status:Int32-&gt;String</c>.</param>
/// <param name="DocumentStep">The C# expression building the document step.</param>
/// <param name="ColumnProperty">The model property whose physical column the migration also changes, if any.</param>
/// <param name="ColumnAction">What it does to that column.</param>
/// <param name="ColumnNumber">For a retype, the C# <c>StoredNumber</c> the column now holds, or <c>null</c> for text.</param>
/// <param name="ColumnEnumNames">For a retype of a former enum to text, the C# member array, or <c>null</c>.</param>
/// <param name="PreviousColumn">For a rename, the former column.</param>
/// <param name="Path">The property's path in the document: <c>Status</c>, <c>Shipping.Line1</c>.</param>
/// <param name="IsTypeChange">Whether the migration changes the property's type, which can leave an index stale.</param>
/// <param name="IndexStoreType">
/// For a type change, the type an index over the property casts its extraction to now (<c>integer</c>), or
/// <see langword="null"/> for text: what an index the schema built over the key is compared with.
/// </param>
/// <docs>fundamentals/perspectives/stored-form-migrations</docs>
/// <tests>tests/Whizbang.Generators.Tests/StoredFormMigrationGenerationTests.cs</tests>
public sealed record StoredFormInfo(
    int Order,
    string NameSuffix,
    string DocumentStep,
    string? ColumnProperty = null,
    StoredFormColumnAction ColumnAction = StoredFormColumnAction.None,
    string ColumnNumber = "null",
    string ColumnEnumNames = "null",
    string? PreviousColumn = null,
    string Path = "",
    bool IsTypeChange = false,
    string? IndexStoreType = null);

/// <summary>A stored-form declaration the generator reports instead of generating.</summary>
/// <param name="Id">WHIZ830 (cannot be generated) or WHIZ832 (inside a collection element).</param>
/// <param name="Subject">The model and path: <c>App.OrderModel.Status</c>.</param>
/// <param name="Reason">Why.</param>
/// <docs>fundamentals/perspectives/stored-form-migrations#diagnostics</docs>
public sealed record StoredFormProblem(string Id, string Subject, string Reason);

/// <summary>
/// Finds a perspective model's stored-form declarations (<c>[StoredForm]</c> on a property, <c>[StoredFormRemoved]</c>
/// on a type) and turns each into a migration the schema generator emits, or a problem it reports.
/// </summary>
/// <remarks>
/// <para>
/// The walk reaches the model's own and inherited properties and the properties of nested objects it reaches through
/// a property, with the path of each (<c>Shipping.Line1</c>): the key a document stores the value under, which is the
/// property's name. A declaration inside an element of a collection has no single path a generated statement could
/// convert, so it is reported (WHIZ832) rather than silently ignored.
/// </para>
/// <para>
/// A type change is generated only between scalar forms whose stored values can be converted value by value: a
/// number, a <see langword="bool"/> or an enum to a string, a string or a number to a number, a string to an enum.
/// Anything else is reported (WHIZ830) with a pointer to the custom migration.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/stored-form-migrations</docs>
/// <tests>tests/Whizbang.Generators.Tests/StoredFormMigrationGenerationTests.cs</tests>
public static class StoredFormDiscovery {
  /// <summary>A declaration the generator cannot turn into SQL.</summary>
  public const string CANNOT_GENERATE = "WHIZ830";

  /// <summary>A declaration inside an element of a collection.</summary>
  public const string INSIDE_COLLECTION = "WHIZ832";

  private const string STEP = "global::Whizbang.Data.Postgres.StoredFormStep.";
  private const string NUMBER = "global::Whizbang.Data.Postgres.StoredNumber.";
  private const string STORED_FORM = "Whizbang.Core.Perspectives.StoredFormAttribute";
  private const string STORED_FORM_REMOVED = "Whizbang.Core.Perspectives.StoredFormRemovedAttribute";
  private const string PHYSICAL_FIELD = "Whizbang.Core.Perspectives.PhysicalFieldAttribute";
  private const string STORAGE = "Whizbang.Core.Perspectives.PerspectiveStorageAttribute";
  private const int MAX_DEPTH = 8;
  private const int SPLIT = 2;

  private enum Scalar { None, Text, Bool, Number, Enum }

  /// <summary>The model's migrations, in the order they run, and the declarations reported instead.</summary>
  /// <param name="model">The perspective's model type.</param>
  public static (ImmutableArray<StoredFormInfo> Migrations, ImmutableArray<StoredFormProblem> Problems) From(INamedTypeSymbol? model) {
    if (model is null) {
      return ([], []);
    }

    var walk = new Walk(TypeNameUtilities.Display(model), model.GetAttributes().Any(a =>
      TypeNameUtilities.IsNamed(a.AttributeClass, STORAGE) && a.ConstructorArguments.Length > 0 && (int)a.ConstructorArguments[0].Value! == SPLIT));
    _walk(walk, model, "", 0, null);

    var ordered = walk.Migrations.Select((m, i) => (m, i)).OrderBy(x => x.m.Order).ThenBy(x => x.i).Select(x => x.m);
    return ([.. ordered], [.. walk.Problems]);
  }

  private sealed class Walk(string model, bool isSplit) {
    public string Model { get; } = model;
    public bool IsSplit { get; } = isSplit;
    public List<StoredFormInfo> Migrations { get; } = [];
    public List<StoredFormProblem> Problems { get; } = [];
    public HashSet<string> OnPath { get; } = new(StringComparer.Ordinal);

    public void Problem(string id, string path, string reason) => Problems.Add(new StoredFormProblem(id, $"{Model}.{path}", reason));
  }

  private static void _walk(Walk walk, ITypeSymbol type, string prefix, int depth, string? collection) {
    var key = TypeNameUtilities.FullyQualified(type);
    if (depth > MAX_DEPTH || !walk.OnPath.Add(key)) {
      return;
    }

    foreach (var removed in type.GetAttributes().Where(a => TypeNameUtilities.IsNamed(a.AttributeClass, STORED_FORM_REMOVED))) {
      _removal(walk, removed, prefix, collection);
    }

    foreach (var property in _properties(type)) {
      var path = prefix + property.Name;
      _declaration(walk, property, path, depth, collection);
      _descend(walk, property, path, depth, collection);
    }

    walk.OnPath.Remove(key);
  }

  private static void _removal(Walk walk, AttributeData removed, string prefix, string? collection) {
    var relative = removed.ConstructorArguments.Length > 0 ? removed.ConstructorArguments[0].Value as string : null;
    var path = prefix + relative;
    if (collection is not null) {
      walk.Problem(INSIDE_COLLECTION, path, $"it is inside an element of the collection {collection}");
    } else if (!_isPath(relative)) {
      walk.Problem(CANNOT_GENERATE, path, "the removed path is not a property key (keys separated by dots)");
    } else {
      walk.Migrations.Add(new StoredFormInfo(3, $"{path}:removed", $"{STEP}Remove({_literal(path)})"));
    }
  }

  private static void _declaration(Walk walk, IPropertySymbol property, string path, int depth, string? collection) {
    var declaration = property.GetAttributes().FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, STORED_FORM));
    if (declaration is null) {
      return;
    }
    if (collection is not null) {
      walk.Problem(INSIDE_COLLECTION, path, $"it is inside an element of the collection {collection}");
    } else {
      _declare(walk, property, path, declaration, topLevel: depth == 0);
    }
  }

  // Into a complex property, or into a collection's complex element, where a declaration is reported, not generated.
  private static void _descend(Walk walk, IPropertySymbol property, string path, int depth, string? collection) {
    if (_elementOf(property.Type) is { } element) {
      if (_isComplex(_unwrap(element))) {
        _walk(walk, _unwrap(element), path + "[].", depth + 1, collection ?? path);
      }
    } else if (_isComplex(_unwrap(property.Type))) {
      _walk(walk, _unwrap(property.Type), path + ".", depth + 1, collection);
    }
  }

  private static void _declare(Walk walk, IPropertySymbol property, string path, AttributeData declaration, bool topLevel) {
    var current = _unwrap(property.Type);
    var physical = topLevel
      ? property.GetAttributes().FirstOrDefault(a => TypeNameUtilities.IsNamed(a.AttributeClass, PHYSICAL_FIELD))
      : null;
    var column = physical is null ? null : property.Name;

    foreach (var argument in declaration.NamedArguments) {
      switch (argument.Key) {
        case "PreviousName":
          _rename(walk, path, argument.Value.Value as string, column, physical);
          break;
        case "Previously":
          _retype(walk, path, argument.Value.Value as ITypeSymbol, current, column);
          break;
        case "DefaultWhenMissing":
          _default(walk, path, argument.Value, current, physical is not null && walk.IsSplit);
          break;
      }
    }
  }

  private static void _rename(Walk walk, string path, string? previous, string? column, AttributeData? physical) {
    if (!_isKey(previous)) {
      walk.Problem(CANNOT_GENERATE, path, $"PreviousName '{previous}' is not a property key");
      return;
    }
    // A declared column name did not change with the property; a derived one did.
    var declaredColumn = physical?.NamedArguments.FirstOrDefault(a => a.Key == "ColumnName").Value.Value as string;
    walk.Migrations.Add(new StoredFormInfo(0, $"{path}:renamed-from:{previous}",
      $"{STEP}Rename({_literal(path)}, {_literal(previous!)})",
      column, column is null ? StoredFormColumnAction.None : StoredFormColumnAction.Rename,
      PreviousColumn: declaredColumn ?? NamingConventionUtilities.ToSnakeCase(previous!)));
  }

  private static void _retype(Walk walk, string path, ITypeSymbol? previously, ITypeSymbol current, string? column) {
    if (previously is null) {
      return;
    }
    var from = _unwrap(previously);
    if (SymbolEqualityComparer.Default.Equals(from, current)) {
      walk.Problem(CANNOT_GENERATE, path, $"Previously is the property's current type ({current.Name})");
      return;
    }

    var (fromKind, _) = _classify(from);
    var (toKind, toNumber) = _classify(current);
    string? step = null;
    var action = StoredFormColumnAction.Retype;
    var number = "null";
    var enumNames = "null";
    switch (fromKind, toKind) {
      case (Scalar.Number or Scalar.Bool, Scalar.Text):
        step = $"{STEP}ToText({_literal(path)})";
        break;
      case (Scalar.Enum, Scalar.Text):
        enumNames = _members(from);
        step = $"{STEP}EnumNumberToName({_literal(path)}, {enumNames})";
        break;
      case (Scalar.Text or Scalar.Number, Scalar.Number):
        number = NUMBER + toNumber;
        step = $"{STEP}ToNumber({_literal(path)}, {number})";
        break;
      case (Scalar.Text, Scalar.Enum):
        if (PhysicalFieldScalar.IsFlagsEnum(current) && toNumber == "UInt64") {
          walk.Problem(CANNOT_GENERATE, path,
            "a [Flags] enum over ulong cannot be converted from a string, because its combinations are computed in bigint");
          return;
        }
        // The enum column rewrite already converts a text column of names to numbers.
        action = StoredFormColumnAction.None;
        step = $"{STEP}ToEnumNumber({_literal(path)}, {_literal(current.Name)}, {NUMBER}{toNumber}, {_members(current)}, "
          + (PhysicalFieldScalar.IsFlagsEnum(current) ? "true" : "false") + ")";
        break;
    }

    if (step is null) {
      walk.Problem(CANNOT_GENERATE, path, $"no generated conversion from {from.Name} to {current.Name}");
      return;
    }
    walk.Migrations.Add(new StoredFormInfo(1, $"{path}:{from.Name}->{current.Name}", step,
      column, column is null ? StoredFormColumnAction.None : action, number, enumNames,
      Path: path, IsTypeChange: true, IndexStoreType: JsonIndexSql.StoreType(JsonIndexDiscovery.CastFor(current) ?? JsonIndexCast.None)));
  }

  private static void _default(Walk walk, string path, TypedConstant value, ITypeSymbol current, bool splitPhysical) {
    if (value.IsNull) {
      return;
    }
    if (splitPhysical) {
      walk.Problem(CANNOT_GENERATE, path,
        "a default on a physical field of a Split model lives only in its column, which a document default cannot reach");
      return;
    }
    var (kind, number) = _classify(current);
    if (kind == Scalar.None) {
      walk.Problem(CANNOT_GENERATE, path, "a default is written only for a string, number, bool or enum property");
      return;
    }
    if (_json(value, kind, number, current) is not { } json) {
      walk.Problem(CANNOT_GENERATE, path, $"the default {value.ToCSharpString()} is not a value of the property's type ({current.Name})");
      return;
    }
    walk.Migrations.Add(new StoredFormInfo(2, $"{path}:default", $"{STEP}DefaultWhenMissing({_literal(path)}, {_literal(json)})"));
  }

  // The default as the document stores it, or null when it is not a value of the property's type.
  private static string? _json(TypedConstant value, Scalar kind, string? number, ITypeSymbol current) {
    if (value.Kind == TypedConstantKind.Enum) {
      return kind == Scalar.Enum && SymbolEqualityComparer.Default.Equals(value.Type, current)
        ? Convert.ToString(value.Value, CultureInfo.InvariantCulture)
        : null;
    }
    if (value.Kind != TypedConstantKind.Primitive) {
      return null;
    }
    return value.Value switch {
      string or char when kind == Scalar.Text => _jsonString(Convert.ToString(value.Value, CultureInfo.InvariantCulture)!),
      bool flag when kind == Scalar.Bool => flag ? "true" : "false",
      string or char or bool => null,
      _ when kind is Scalar.Number or Scalar.Enum => _jsonNumber(value.Value, kind, number),
      _ => null,
    };
  }

  // A numeric default as JSON, or null when it is not finite or has a fraction the property's type cannot hold.
  private static string? _jsonNumber(object? raw, Scalar kind, string? number) {
    var d = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
    var integral = kind == Scalar.Enum || number is not ("Decimal" or "Single" or "Double");
    if (double.IsNaN(d) || double.IsInfinity(d) || (integral && Math.Abs(d % 1) > 0)) {
      return null;
    }
    return raw is double or float
      ? d.ToString("R", CultureInfo.InvariantCulture)
      : Convert.ToString(raw, CultureInfo.InvariantCulture);
  }

  private static (Scalar Kind, string? Number) _classify(ITypeSymbol type) {
    if (type.TypeKind == TypeKind.Enum) {
      // An enum is a named type with an underlying integral type.
      return (Scalar.Enum, _number(((INamedTypeSymbol)type).EnumUnderlyingType!.SpecialType));
    }
    return type.SpecialType switch {
      SpecialType.System_String or SpecialType.System_Char => (Scalar.Text, null),
      SpecialType.System_Boolean => (Scalar.Bool, null),
      _ when _number(type.SpecialType) is { } n => (Scalar.Number, n),
      _ => (Scalar.None, null),
    };
  }

  private static string? _number(SpecialType type) => type switch {
    SpecialType.System_SByte => "SByte",
    SpecialType.System_Byte => "Byte",
    SpecialType.System_Int16 => "Int16",
    SpecialType.System_UInt16 => "UInt16",
    SpecialType.System_Int32 => "Int32",
    SpecialType.System_UInt32 => "UInt32",
    SpecialType.System_Int64 => "Int64",
    SpecialType.System_UInt64 => "UInt64",
    SpecialType.System_Decimal => "Decimal",
    SpecialType.System_Single => "Single",
    SpecialType.System_Double => "Double",
    _ => null,
  };

  private static string _members(ITypeSymbol enumType) {
    // Only an enum reaches here, and EnumMembers answers null only for a type that is not one.
    var members = PhysicalFieldScalar.EnumMembers(enumType)!;
    string[] pairs = members.Length == 0
      ? []
      : [.. members.Split(';').Select(m => {
        var at = m.IndexOf('=');
        return $"({_literal(m[..at])}, {_literal(m[(at + 1)..])})";
      })];
    return pairs.Length == 0
      ? "new (string Name, string Value)[] { }"
      : $"new (string Name, string Value)[] {{ {string.Join(", ", pairs)} }}";
  }

  // Public instance properties with a getter, the type's own first, then its base types'.
  private static IEnumerable<IPropertySymbol> _properties(ITypeSymbol type) {
    var seen = new HashSet<string>(StringComparer.Ordinal);
    for (var current = type as INamedTypeSymbol; current is { SpecialType: SpecialType.None }; current = current.BaseType) {
      foreach (var property in current.GetMembers().OfType<IPropertySymbol>()) {
        if (!property.IsStatic && !property.IsIndexer && property.DeclaredAccessibility == Accessibility.Public
            && property.GetMethod is not null && seen.Add(property.Name)) {
          yield return property;
        }
      }
    }
  }

  private static ITypeSymbol? _elementOf(ITypeSymbol type) {
    if (type is IArrayTypeSymbol array) {
      return array.ElementType;
    }
    if (type is INamedTypeSymbol { IsGenericType: true } named && type.SpecialType != SpecialType.System_String
        && TypeNameUtilities.Display(named.ContainingNamespace).StartsWith("System.Collections", StringComparison.Ordinal)) {
      return named.TypeArguments[^1];
    }
    return null;
  }

  private static bool _isComplex(ITypeSymbol type) =>
    type.TypeKind is TypeKind.Class or TypeKind.Struct
    && type.SpecialType == SpecialType.None
    && !TypeNameUtilities.Display(type).StartsWith("System.", StringComparison.Ordinal);

  private static ITypeSymbol _unwrap(ITypeSymbol type) =>
    type is INamedTypeSymbol { IsGenericType: true } nullable && nullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
      ? nullable.TypeArguments[0]
      : type;

  private static bool _isKey(string? key) =>
    !string.IsNullOrEmpty(key) && key!.All(c => (c < 128 && char.IsLetterOrDigit(c)) || c == '_');

  private static bool _isPath(string? path) => !string.IsNullOrEmpty(path) && path!.Split('.').All(_isKey);

  private static string _literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

  private static string _jsonString(string value) {
    var sb = new StringBuilder("\"");
    foreach (var c in value) {
      switch (c) {
        case '"':
          sb.Append("\\\"");
          break;
        case '\\':
          sb.Append("\\\\");
          break;
        default:
          if (c < ' ') {
            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
          } else {
            sb.Append(c);
          }
          break;
      }
    }
    return sb.Append('"').ToString();
  }
}
