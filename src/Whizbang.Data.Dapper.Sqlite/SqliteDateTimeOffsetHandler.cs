using System.Data;
using Dapper;

namespace Whizbang.Data.Dapper.Sqlite;

/// <summary>
/// Dapper type handler for DateTimeOffset with SQLite.
/// SQLite stores DateTimeOffset as TEXT, so we need to handle conversion.
/// </summary>
public class SqliteDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset> {
  public override DateTimeOffset Parse(object value) {
    if (value is string str) {
      return DateTimeOffset.Parse(str);
    }
    if (value is DateTimeOffset dto) {
      return dto;
    }
    throw new InvalidCastException($"Cannot convert {value?.GetType()} to DateTimeOffset");
  }

  public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) {
    parameter.Value = value.ToString("O"); // ISO 8601 format
  }

  /// <summary>
  /// Registers this type handler with Dapper.
  /// Call this once at application startup.
  /// </summary>
  public static void Register() {
    SqlMapper.AddTypeHandler(new SqliteDateTimeOffsetHandler());
  }
}
