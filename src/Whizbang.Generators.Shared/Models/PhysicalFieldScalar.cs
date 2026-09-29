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
    if (type is INamedTypeSymbol { IsGenericType: true } nullable
        && nullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T) {
      type = nullable.TypeArguments[0];
    }
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
}
