using System.Data;
using Microsoft.Data.Sqlite;
using Whizbang.Core.Data;

namespace Whizbang.Data.Tests;

/// <summary>
/// Test-only connection factory that reuses a single SQLite connection.
/// Required for in-memory SQLite databases which only persist for a single connection.
/// </summary>
public class SharedSqliteConnectionFactory : IDbConnectionFactory {
  private readonly SqliteConnection _sharedConnection;

  public SharedSqliteConnectionFactory(SqliteConnection sharedConnection) {
    ArgumentNullException.ThrowIfNull(sharedConnection);
    _sharedConnection = sharedConnection;
  }

  public Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default) {
    // Return a non-disposable wrapper around the shared connection
    // This prevents using statements from closing our in-memory database
    return Task.FromResult<IDbConnection>(new NonDisposableSqliteConnection(_sharedConnection));
  }
}
