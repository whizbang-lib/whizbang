using System.Data;
using Microsoft.Data.Sqlite;
using Whizbang.Core.Data;

namespace Whizbang.Data.Tests;

/// <summary>
/// SQLite connection factory for integration tests.
/// Creates in-memory SQLite databases for fast, isolated testing.
/// </summary>
public class SqliteConnectionFactory(string connectionString = "Data Source=:memory:") : IDbConnectionFactory {
  private readonly string _connectionString = connectionString;

  public Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default) {
    var connection = new SqliteConnection(_connectionString);
    return Task.FromResult<IDbConnection>(connection);
  }
}
