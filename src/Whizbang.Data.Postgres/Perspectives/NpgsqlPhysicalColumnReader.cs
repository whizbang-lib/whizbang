// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Npgsql;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Perspectives;

/// <summary>
/// Reads a Split model's promoted columns from the row the document was read from. The columns follow the
/// document, in the order the model's <see cref="SplitPhysicalFieldMap{TModel}"/> lists them.
/// </summary>
/// <remarks>
/// Reads each value as the property's own type, which the driver converts natively for everything the store
/// writes natively. An enum is the exception: its column holds the underlying number (a <c>ulong</c>-backed one as
/// <c>numeric</c>), which is converted back to the member, and a column still holding names is parsed from them.
/// Shared by the Dapper store and the EF Core store's read of a shadow table during a blue-green rebuild, which
/// reads the row with SQL because EF Core cannot map a JSON complex property over raw SQL.
/// </remarks>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/DapperSplitPhysicalFieldReloadTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/BlueGreenRebuildIntegrationTests.cs</tests>
public sealed class NpgsqlPhysicalColumnReader(
    NpgsqlDataReader reader, IReadOnlyList<SplitPhysicalColumn> columns, JsonSerializerOptions jsonOptions)
    : IPhysicalColumnReader {

  /// <inheritdoc/>
  public T Read<T>(string column) {
    var ordinal = _ordinal(column);
    if (reader.IsDBNull(ordinal)) {
      return default!;
    }
    // A jsonb column is read with the options it was written with, the store's, rather than the
    // driver's: the driver can only map an object or a list by dynamic JSON, which this connection
    // does not enable and which would not apply the persistence profile if it did.
    if (string.Equals(reader.GetDataTypeName(ordinal), "jsonb", StringComparison.Ordinal)) {
      return JsonSerializer.Deserialize(reader.GetString(ordinal), (JsonTypeInfo<T>)jsonOptions.GetTypeInfo(typeof(T)))!;
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
