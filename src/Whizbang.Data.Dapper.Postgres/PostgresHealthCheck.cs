using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Whizbang.Core.Data;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Health check for PostgreSQL connectivity.
/// Verifies that the database connection can be opened and a simple query executes successfully.
/// </summary>
public class PostgresHealthCheck(IDbConnectionFactory connectionFactory) : IHealthCheck {
  private readonly IDbConnectionFactory _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

  public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) {
    try {
      using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
      // Connection is already opened by PostgresConnectionFactory

      // Execute simple query to verify database is accessible
      // Use Dapper extension method for compatibility with IDbConnection
      _ = await connection.ExecuteScalarAsync("SELECT 1");

      return HealthCheckResult.Healthy("PostgreSQL database is accessible");
    } catch (Exception ex) {
      return HealthCheckResult.Unhealthy("PostgreSQL database is not accessible", ex);
    }
  }
}
