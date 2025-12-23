using System.Data;
using System.Globalization;
using Dapper;

namespace Whizbang.Data.Dapper.Sqlite;

/// <summary>
/// Dapper type handler for DateTimeOffset with SQLite.
/// SQLite stores DateTimeOffset as TEXT, so we need to handle conversion.
/// </summary>
/// <tests>tests/Whizbang.Data.Tests/DapperTestBase.cs:SetupAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs</tests>
public class SqliteDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset> {
  /// <summary>
  /// Parses a database value to a DateTimeOffset, handling both string and DateTimeOffset types.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs</tests>
  public override DateTimeOffset Parse(object value) {
    if (value is string str) {
      return DateTimeOffset.Parse(str, CultureInfo.InvariantCulture);
    }
    if (value is DateTimeOffset dto) {
      return dto;
    }
    throw new InvalidCastException($"Cannot convert {value?.GetType()} to DateTimeOffset");
  }

  /// <summary>
  /// Sets a DateTimeOffset value on a database parameter, converting it to ISO 8601 format for SQLite storage.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs</tests>
  public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) {
    parameter.Value = value.ToString("O"); // ISO 8601 format
  }

  /// <summary>
  /// Registers this type handler with Dapper.
  /// Call this once at application startup.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperTestBase.cs:SetupAsync</tests>
  public static void Register() {
    SqlMapper.AddTypeHandler(new SqliteDateTimeOffsetHandler());
  }
}
