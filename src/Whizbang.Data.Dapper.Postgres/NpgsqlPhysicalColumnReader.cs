using Npgsql;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Reads a Split model's promoted columns from the row the document was read from. The columns follow the
/// document, in the order the model's <see cref="SplitPhysicalFieldMap{TModel}"/> lists them.
/// </summary>
/// <remarks>
/// Reads each value as the property's own type, which the driver converts natively for everything the store
/// writes natively. An enum is the exception: its column holds the underlying number (a <c>ulong</c>-backed one as
/// <c>numeric</c>), which is converted back to the member, and a column still holding names is parsed from them.
/// </remarks>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/DapperSplitPhysicalFieldReloadTests.cs</tests>
internal sealed class NpgsqlPhysicalColumnReader(NpgsqlDataReader reader, IReadOnlyList<SplitPhysicalColumn> columns)
    : IPhysicalColumnReader {

  /// <inheritdoc/>
  public T Read<T>(string column) {
    var ordinal = _ordinal(column);
    if (reader.IsDBNull(ordinal)) {
      return default!;
    }
    var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
    return type.IsEnum
      ? (T)_enumValue(type, reader.GetValue(ordinal))
      : reader.GetFieldValue<T>(ordinal);
  }

  private static object _enumValue(Type enumType, object stored) => stored switch {
    string name => Enum.Parse(enumType, name),
    decimal number => Enum.ToObject(enumType, decimal.ToUInt64(number)),
    _ => Enum.ToObject(enumType, stored),
  };

  /// <inheritdoc/>
  public float[]? GetVector(string column) {
    var ordinal = _ordinal(column);
    return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<float[]>(ordinal);
  }

  private int _ordinal(string column) {
    for (var i = 0; i < columns.Count; i++) {
      if (columns[i].Name == column) {
        return i + 1;
      }
    }
    throw new InvalidOperationException($"'{column}' is not one of the promoted columns this row was read with.");
  }
}
