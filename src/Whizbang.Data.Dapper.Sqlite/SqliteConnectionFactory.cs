using System.Data;
using Microsoft.Data.Sqlite;
using Whizbang.Core.Data;

namespace Whizbang.Data.Dapper.Sqlite;

/// <summary>
/// SQLite-specific implementation of IDbConnectionFactory.
/// </summary>
public class SqliteConnectionFactory : IDbConnectionFactory {
  private readonly string _connectionString;

  public SqliteConnectionFactory(string connectionString) {
    ArgumentNullException.ThrowIfNull(connectionString);
    _connectionString = connectionString;
  }

  public Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default) {
    var connection = new SqliteConnection(_connectionString);
    return Task.FromResult<IDbConnection>(connection);
  }
}
