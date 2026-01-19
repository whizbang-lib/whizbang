using System.Data;

namespace Whizbang.Core.Data;

/// <summary>
/// ORM-agnostic database executor.
/// Provides query and execute operations that can be implemented using
/// different ORMs (Dapper, Entity Framework, NHibernate, etc.).
/// </summary>
/// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresEventStoreTests.cs</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresEventStore.RetryTests.cs</tests>
public interface IDbExecutor {
  /// <summary>
  /// Executes a query and returns a collection of results.
  /// </summary>
  /// <typeparam name="T">The type of objects to return</typeparam>
  /// <param name="connection">The database connection</param>
  /// <param name="sql">The SQL query to execute</param>
  /// <param name="param">The query parameters (optional)</param>
  /// <param name="transaction">The transaction to use (optional)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Collection of query results</returns>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresEventStoreTests.cs</tests>
  Task<IReadOnlyList<T>> QueryAsync<T>(
    IDbConnection connection,
    string sql,
    object? param = null,
    IDbTransaction? transaction = null,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Executes a query and returns a single result, or null if not found.
  /// </summary>
  /// <typeparam name="T">The type of object to return</typeparam>
  /// <param name="connection">The database connection</param>
  /// <param name="sql">The SQL query to execute</param>
  /// <param name="param">The query parameters (optional)</param>
  /// <param name="transaction">The transaction to use (optional)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Single query result, or null if not found</returns>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs</tests>
  Task<T?> QuerySingleOrDefaultAsync<T>(
    IDbConnection connection,
    string sql,
    object? param = null,
    IDbTransaction? transaction = null,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Executes a command (INSERT, UPDATE, DELETE) and returns the number of affected rows.
  /// </summary>
  /// <param name="connection">The database connection</param>
  /// <param name="sql">The SQL command to execute</param>
  /// <param name="param">The command parameters (optional)</param>
  /// <param name="transaction">The transaction to use (optional)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Number of rows affected</returns>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresEventStoreTests.cs</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresRequestResponseStoreTests.cs</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresEventStore.RetryTests.cs</tests>
  Task<int> ExecuteAsync(
    IDbConnection connection,
    string sql,
    object? param = null,
    IDbTransaction? transaction = null,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Executes a command and returns a scalar value.
  /// </summary>
  /// <typeparam name="T">The type of value to return</typeparam>
  /// <param name="connection">The database connection</param>
  /// <param name="sql">The SQL command to execute</param>
  /// <param name="param">The command parameters (optional)</param>
  /// <param name="transaction">The transaction to use (optional)</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Scalar result value</returns>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/DapperPostgresSequenceProviderTests.cs</tests>
  Task<T?> ExecuteScalarAsync<T>(
    IDbConnection connection,
    string sql,
    object? param = null,
    IDbTransaction? transaction = null,
    CancellationToken cancellationToken = default);
}
