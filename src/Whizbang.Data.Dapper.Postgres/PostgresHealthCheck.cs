using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Whizbang.Core.Data;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Health check for PostgreSQL connectivity.
/// Verifies that the database connection can be opened and a simple query executes successfully.
/// </summary>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresHealthCheckTests.cs</tests>
public class PostgresHealthCheck(IDbConnectionFactory connectionFactory) : IHealthCheck {
  private readonly IDbConnectionFactory _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

  /// <summary>
  /// Checks the health of the PostgreSQL database by verifying connectivity and executing a simple query.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresHealthCheckTests.cs:CheckHealthAsync_AgainstALiveDatabase_ReportsHealthyAsync</tests>
  public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) {
    try {
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      // Connection is already opened by PostgresConnectionFactory

      // Execute simple query to verify database is accessible.
      // CommandDefinition rather than the string overload: the string overload takes no
      // token, so a probe issued during shutdown would run to completion against a server
      // that may already be unreachable, holding the health pipeline open behind it.
      _ = await connection.ExecuteScalarAsync(
        new CommandDefinition("SELECT 1", cancellationToken: cancellationToken));

      return HealthCheckResult.Healthy("PostgreSQL database is accessible");
    } catch (Exception ex) {
      return HealthCheckResult.Unhealthy("PostgreSQL database is not accessible", ex);
    }
  }
}
