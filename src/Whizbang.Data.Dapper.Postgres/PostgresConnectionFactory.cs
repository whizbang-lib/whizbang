using System.Data;
using Npgsql;
using Whizbang.Core.Data;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// PostgreSQL-specific implementation of IDbConnectionFactory.
/// Returns connections that are already opened to ensure proper async initialization.
/// </summary>
public class PostgresConnectionFactory : IDbConnectionFactory {
  private readonly string _connectionString;

  public PostgresConnectionFactory(string connectionString) {
    ArgumentNullException.ThrowIfNull(connectionString);
    _connectionString = connectionString;
  }

  public async Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default) {
    var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    return connection;
  }
}
