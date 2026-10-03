using System.Linq;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// The column scalar for an enumeration promoted to a physical column: its underlying number, widened where
/// Postgres has no matching integer (byte-sized to <c>smallint</c>, unsigned to the next wider signed type,
/// <see cref="ulong"/> to <c>numeric</c>). Must agree with <c>PerspectivePhysicalValues.ColumnScalarType</c>, which
/// applies the same widening at run time.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Generators.Tests/EnumPhysicalFieldGenerationTests.cs</tests>
public static class PhysicalFieldScalar {
  /// <summary>
  /// The fully qualified CLR name (without <c>global::</c>) of the scalar an enumeration's column holds, or null
  /// when <paramref name="type"/> is not an enumeration (a nullable enumeration counts).
  /// </summary>
  public static string? EnumColumnScalar(ITypeSymbol type) {
    type = _unwrapNullable(type);
    if (type.TypeKind != TypeKind.Enum || type is not INamedTypeSymbol { EnumUnderlyingType: { } underlying }) {
      return null;
    }
    return underlying.SpecialType switch {
      SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 => "System.Int16",
      SpecialType.System_UInt16 or SpecialType.System_Int32 => "System.Int32",
      SpecialType.System_UInt32 or SpecialType.System_Int64 => "System.Int64",
      _ => "System.Decimal",
    };
  }

  /// <summary>
  /// An enumeration's members as <c>Name=Value;Name=Value</c>, in declaration order, for the rewrite that converts a
  /// column of names to numbers; null when <paramref name="type"/> is not an enumeration. A string rather than a
  /// collection so the record carrying it keeps value equality for the incremental cache.
  /// </summary>
  public static string? EnumMembers(ITypeSymbol type) {
    type = _unwrapNullable(type);
    if (type.TypeKind != TypeKind.Enum) {
      return null;
    }
    var members = type.GetMembers()
      .OfType<IFieldSymbol>()
      .Where(f => f.HasConstantValue)
      .Select(f => f.Name + "=" + System.Convert.ToString(f.ConstantValue, System.Globalization.CultureInfo.InvariantCulture));
    return string.Join(";", members);
  }

  /// <summary>
  /// Whether <paramref name="type"/> is an enumeration marked <c>[Flags]</c> (a nullable one counts), whose stored
  /// names may be combined (<c>"A, B"</c>) and are converted to the bitwise OR of their values.
  /// </summary>
  public static bool IsFlagsEnum(ITypeSymbol type) {
    type = _unwrapNullable(type);
    return type.TypeKind == TypeKind.Enum
      && type.GetAttributes().Any(a => a.AttributeClass is {
        Name: "FlagsAttribute", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true },
      });
  }

  /// <summary>The column type a promoted object, collection or dictionary gets when the author declares none.</summary>
  public const string JSONB = "jsonb";

  /// <summary>
  /// The column type a field gets when <c>[PhysicalField]</c> declares none and the CLR type has no scalar
  /// column: <c>jsonb</c> for an object, a record, a user struct, a collection or a dictionary, and null for
  /// everything the mapping tables already type.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Decided once here, at discovery, so every generator that describes the column reads the same
  /// declared type: both schema generators, the EF Core model and the runner's registration. Before
  /// this each mapping table fell through to text, so a list became a column holding its type's name.
  /// </para>
  /// <para>
  /// Left to the mapping tables: special types (primitives, <c>string</c>, <c>decimal</c>,
  /// <c>DateTime</c>), enumerations, the framework's own value types in <c>System</c> (<c>Guid</c>,
  /// <c>DateTimeOffset</c>, <c>DateOnly</c>, <c>TimeOnly</c>, <c>TimeSpan</c>), and the arrays with a
  /// native column of their own: <c>byte[]</c>, <c>float[]</c> and <c>double[]</c>.
  /// </para>
  /// </remarks>
  /// <param name="type">The property's type.</param>
  /// <returns><c>jsonb</c>, or null to leave the type to the mapping tables.</returns>
  /// <tests>tests/Whizbang.Generators.Tests/PhysicalJsonbColumnGenerationTests.cs</tests>
  public static string? DefaultColumnType(ITypeSymbol type) {
    type = _unwrapNullable(type);

    if (type is IArrayTypeSymbol array) {
      return array.ElementType.SpecialType is SpecialType.System_Byte or SpecialType.System_Single or SpecialType.System_Double
        ? null
        : JSONB;
    }

    var structured = type.SpecialType == SpecialType.None
      && type.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface
      && type.ContainingNamespace is not { Name: "System", ContainingNamespace.IsGlobalNamespace: true };

    return structured ? JSONB : null;
  }

  /// <summary>Whether a column type is jsonb, as declared or defaulted, ignoring case and padding.</summary>
  /// <param name="columnType">The column type, or null.</param>
  /// <returns>True for <c>jsonb</c>.</returns>
  public static bool IsJsonb(string? columnType) =>
    string.Equals(columnType?.Trim(), JSONB, System.StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// Whether a promoted field gets a containment index: it declares <c>IndexKinds.Containment</c> and its column
  /// is jsonb, the only column a <c>jsonb_path_ops</c> index can be built over.
  /// </summary>
  /// <param name="declaredKind">The field's declared index kinds, combined.</param>
  /// <param name="columnType">The field's column type, declared or defaulted.</param>
  /// <returns>True when the index is built.</returns>
  public static bool IsContainmentIndexed(int declaredKind, string? columnType) =>
    JsonIndexDiscovery.IncludesContainment(declaredKind) && IsJsonb(columnType);

  private static ITypeSymbol _unwrapNullable(ITypeSymbol type) =>
    type is INamedTypeSymbol { IsGenericType: true } nullable
      && nullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
        ? nullable.TypeArguments[0]
        : type;
}
