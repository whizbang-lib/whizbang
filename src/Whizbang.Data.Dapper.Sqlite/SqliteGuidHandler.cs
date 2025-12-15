using System.Data;
using Dapper;

namespace Whizbang.Data.Dapper.Sqlite;

/// <summary>
/// Dapper type handler for Guid with SQLite.
/// SQLite stores Guids as TEXT, so we need to handle conversion.
/// </summary>
/// <tests>tests/Whizbang.Data.Tests/DapperTestBase.cs:SetupAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs</tests>
public class SqliteGuidHandler : SqlMapper.TypeHandler<Guid> {
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs</tests>
  public override Guid Parse(object value) {
    if (value is string str) {
      return Guid.Parse(str);
    }
    if (value is Guid guid) {
      return guid;
    }
    throw new InvalidCastException($"Cannot convert {value?.GetType()} to Guid");
  }

  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs</tests>
  public override void SetValue(IDbDataParameter parameter, Guid value) {
    parameter.Value = value.ToString();
  }

  /// <summary>
  /// Registers this type handler with Dapper.
  /// Call this once at application startup.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperTestBase.cs:SetupAsync</tests>
  public static void Register() {
    SqlMapper.AddTypeHandler(new SqliteGuidHandler());
  }
}
