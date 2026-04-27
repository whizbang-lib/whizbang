using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Compatibility shim for fixtures and tests still constructing the legacy readiness check.
/// Always returns <c>true</c>; workers gate startup via <c>ISchemaReadyGate</c>.
/// </summary>
public sealed class PostgresDatabaseReadinessCheck : IDatabaseReadinessCheck {
  /// <summary>Constructor accepting a connection string for source compatibility.</summary>
  public PostgresDatabaseReadinessCheck(string connectionString, ILogger? logger = null) {
    _ = connectionString;
    _ = logger;
  }

  /// <summary>Constructor accepting an NpgsqlDataSource for source compatibility.</summary>
  public PostgresDatabaseReadinessCheck(NpgsqlDataSource dataSource, ILogger? logger = null) {
    _ = dataSource;
    _ = logger;
  }

  /// <inheritdoc />
  public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
}
