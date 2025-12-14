using System.Data;

namespace Whizbang.Core.Data;

/// <summary>
/// Factory for creating database connections.
/// Abstracts connection creation to allow swapping between different databases
/// (PostgreSQL, SQL Server, SQLite, etc.) and different ORMs.
/// </summary>
/// <tests>tests/Whizbang.Data.Tests/SqliteConnectionFactory.cs:CreateConnectionAsync</tests>
/// <tests>tests/Whizbang.Data.Tests/SharedSqliteConnectionFactory.cs:CreateConnectionAsync</tests>
public interface IDbConnectionFactory {
  /// <summary>
  /// Creates a new database connection.
  /// The connection is not opened automatically - caller must open it.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>A new database connection instance</returns>
  /// <tests>tests/Whizbang.Data.Tests/SqliteConnectionFactory.cs:CreateConnectionAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/SharedSqliteConnectionFactory.cs:CreateConnectionAsync</tests>
  Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default);
}
