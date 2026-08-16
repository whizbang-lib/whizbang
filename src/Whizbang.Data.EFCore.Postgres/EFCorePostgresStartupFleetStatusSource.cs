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
    var table = string.IsNullOrWhiteSpace(schema) || schema == "public"
      ? "wh_service_instances"
      : $"\"{schema}\".wh_service_instances";

    await using var connectionScope = await Whizbang.Data.Postgres.CoordinatorConnectionScope.AcquireForEfCoreAsync(
      (NpgsqlConnection)dbContext.Database.GetDbConnection(), cancellationToken).ConfigureAwait(false);
    await using var cmd = connectionScope.Connection.CreateCommand();
#pragma warning disable S2077 // schema comes from the EF model, not user input — same pattern as the coordinator
    cmd.CommandText = $@"
      SELECT instance_id, service_name, host_name, last_heartbeat_at
      FROM {table}
      ORDER BY last_heartbeat_at DESC
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
          : DateTimeOffset.MinValue));
    }
    return rows;
  }
}
