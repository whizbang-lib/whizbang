using Npgsql;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Reads a Split model's promoted columns from the row the document was read from. The columns follow the
/// document, in the order the model's <see cref="SplitPhysicalFieldMap{TModel}"/> lists them.
/// </summary>
/// <remarks>
/// Reads each value as the property's own type, which the driver converts natively for everything the store
/// writes natively. An enum is the exception: the store writes it as its name, so it is parsed back from it.
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
      ? (T)Enum.Parse(type, reader.GetString(ordinal))
      : reader.GetFieldValue<T>(ordinal);
  }

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
