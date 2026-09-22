using System.Data;
using Microsoft.Data.Sqlite;
using Whizbang.Core.Data;

namespace Whizbang.Data.Dapper.Sqlite;

/// <summary>
/// SQLite-specific implementation of IDbConnectionFactory.
/// </summary>
/// <tests>tests/Whizbang.Data.Tests/DapperSqliteConnectionFactoryTests.cs</tests>
public class SqliteConnectionFactory : IDbConnectionFactory {
  private readonly string _connectionString;

  /// <summary>
  /// Initializes a new instance of the SqliteConnectionFactory with the specified connection string.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperSqliteConnectionFactoryTests.cs:Constructor_WithNullConnectionString_ThrowsArgumentNullExceptionAsync</tests>
  public SqliteConnectionFactory(string connectionString) {
    ArgumentNullException.ThrowIfNull(connectionString);
    _connectionString = connectionString;
  }

  /// <summary>
  /// Creates a new SQLite database connection asynchronously.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperSqliteConnectionFactoryTests.cs:CreateConnectionAsync_ReturnsSqliteConnectionAsync</tests>
  public Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default) {
    var connection = new SqliteConnection(_connectionString);
    return Task.FromResult<IDbConnection>(connection);
  }
}
