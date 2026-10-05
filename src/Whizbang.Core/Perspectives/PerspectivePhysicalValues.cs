// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// The one conversion from a model value to the scalar a physical column holds. An enumeration in a physical
/// column is stored as its underlying number, widened where Postgres has no matching integer: a byte-sized
/// underlying type becomes <c>smallint</c>, an unsigned one the next wider signed type, and <see cref="ulong"/>
/// <c>numeric</c>. The per-event upsert, the collective apply and replay all bind through here, and the source
/// generator applies the same widening when it declares the column, so the three agree on the value.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectivePhysicalValuesTests.cs</tests>
public static class PerspectivePhysicalValues {
  /// <summary>
  /// The column value for <paramref name="value"/>: an enumeration becomes its underlying number (as
  /// <paramref name="scalarType"/> when one is given, otherwise as <see cref="ColumnScalarType"/> of its underlying
  /// type); anything else is returned unchanged.
  /// </summary>
  public static object? ToColumnScalar(object? value, Type? scalarType = null) {
    if (value is not Enum e) {
      return value;
    }
    var target = scalarType ?? ColumnScalarType(Enum.GetUnderlyingType(e.GetType()));
    return Convert.ChangeType(e, target, CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// The scalar type a column holds for an enumeration with the given underlying integral type.
  /// </summary>
  public static Type ColumnScalarType(Type underlyingType) {
    ArgumentNullException.ThrowIfNull(underlyingType);
    if (underlyingType == typeof(byte) || underlyingType == typeof(sbyte) || underlyingType == typeof(short)) {
      return typeof(short);
    }
    if (underlyingType == typeof(ushort) || underlyingType == typeof(int)) {
      return typeof(int);
    }
    if (underlyingType == typeof(uint) || underlyingType == typeof(long)) {
      return typeof(long);
    }
    if (underlyingType == typeof(ulong)) {
      return typeof(decimal);
    }
    throw new ArgumentException($"{underlyingType.Name} is not an enumeration's underlying integral type.", nameof(underlyingType));
  }
}
