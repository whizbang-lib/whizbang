using System.Data;
using Npgsql;
using Whizbang.Core.Data;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// PostgreSQL-specific implementation of IDbConnectionFactory.
/// Returns connections that are already opened to ensure proper async initialization.
/// </summary>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresTestBase.cs:SetupAsync</tests>
/// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresTestBase.cs:InitializeDatabaseAsync</tests>
public class PostgresConnectionFactory : IDbConnectionFactory {
  private readonly string _connectionString;

  /// <summary>
  /// Initializes a new instance of the PostgresConnectionFactory with the specified connection string.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresTestBase.cs:SetupAsync</tests>
  public PostgresConnectionFactory(string connectionString) {
    ArgumentNullException.ThrowIfNull(connectionString);
    _connectionString = connectionString;
  }

  /// <summary>
  /// Creates and opens a new PostgreSQL database connection asynchronously.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresTestBase.cs:InitializeDatabaseAsync</tests>
  /// <tests>tests/Whizbang.Data.Postgres.Tests/PostgresDatabaseReadinessCheckTests.cs:IsReadyAsync_ChecksAllRequiredTables_VerifiesInboxOutboxEventStoreAsync</tests>
  public async Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default) {
    var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);
    return connection;
  }
}
