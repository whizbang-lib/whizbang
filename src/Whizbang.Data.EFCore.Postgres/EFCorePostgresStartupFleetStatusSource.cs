using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Whizbang.Core.Messaging;
using Whizbang.Core.Startup;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The Postgres fleet source for the startup status surface: reads <c>wh_service_instances</c> so
/// the fleet section can report every live instance and how long since each was last heard from.
/// Supplied by the driver because the fleet lives in the database and only a driver knows how to
/// reach it; the surface treats a throw as "fleet unavailable", stated with a reason — during a
/// cold-boot migration this table may not exist yet, and that is an honest answer, not an error.
/// </summary>
/// <docs>proposals/startup-pipeline#status</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupFleetStatusSourceTests.cs</tests>
public sealed class EFCorePostgresStartupFleetStatusSource : IStartupFleetStatusSource {
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly Type _dbContextType;

  /// <summary>Creates the source over the consumer's DbContext type, resolved per call from a fresh scope.</summary>
  public EFCorePostgresStartupFleetStatusSource(IServiceScopeFactory scopeFactory, Type dbContextType) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(dbContextType);
    _scopeFactory = scopeFactory;
    _dbContextType = dbContextType;
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<FleetInstanceStatus>> GetFleetAsync(CancellationToken cancellationToken) {
    using var scope = _scopeFactory.CreateScope();
    var dbContext = (DbContext)scope.ServiceProvider.GetRequiredService(_dbContextType);

    var schema = dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();
    var prefix = string.IsNullOrWhiteSpace(schema) || schema == "public" ? "" : $"\"{schema}\".";

    await using var connectionScope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
      (NpgsqlConnection)dbContext.Database.GetDbConnection(), cancellationToken).ConfigureAwait(false);
    await using var cmd = connectionScope.Connection.CreateCommand();
#pragma warning disable S2077 // schema comes from the EF model, not user input — same pattern as the coordinator
    // Capabilities ride along as a join, not a fan-out — "which instance is the migrator right
    // now" answered from the recorded holdings (derived state: the lock decides, the row reports).
    cmd.CommandText = $@"
      SELECT i.instance_id, i.service_name, i.host_name, i.last_heartbeat_at,
             COALESCE(array_agg(c.capability ORDER BY c.capability) FILTER (WHERE c.capability IS NOT NULL), '{{}}') AS capabilities,
             i.lifecycle_phase, i.library_version
      FROM {prefix}wh_service_instances i
      LEFT JOIN {prefix}wh_instance_capabilities c ON c.instance_id = i.instance_id
      GROUP BY i.instance_id, i.service_name, i.host_name, i.last_heartbeat_at, i.lifecycle_phase, i.library_version
      ORDER BY i.last_heartbeat_at DESC
      LIMIT 200";
#pragma warning restore S2077

    var rows = new List<FleetInstanceStatus>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      rows.Add(new FleetInstanceStatus(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetFieldValue<DateTime>(3) is { } heardAt
          ? new DateTimeOffset(DateTime.SpecifyKind(heardAt, DateTimeKind.Utc))
          : DateTimeOffset.MinValue,
        reader.GetFieldValue<string[]>(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6)));
    }
    return rows;
  }
}
