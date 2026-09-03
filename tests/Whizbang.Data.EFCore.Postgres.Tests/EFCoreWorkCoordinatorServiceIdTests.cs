using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Configuration;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>The local service identity (<c>wh_service_config.service_id</c>) rides on every published
/// envelope as <c>SourceServiceId</c>. The lookup was the one query in the coordinator that named
/// its table bare, so on any non-public schema it resolved through <c>search_path</c>: <c>42P01</c>
/// once a minute from the integrity checkpoint worker, and — the half that matters — a swallowed
/// failure at publish time that stamped every envelope with an empty source id (issue #630).</para>
/// <para>The test database also has <c>public.wh_service_config</c> (the framework migrations ran
/// there), so a bare query does not fail here — it silently reads the WRONG row. That is the
/// stronger assertion: the id must come from the schema the model declares.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Integration")]
[Category("Shard1")]
public class EFCoreWorkCoordinatorServiceIdTests : EFCoreTestBase {
  private const string ServiceSchema = "svc_identity";

  /// <summary>
  /// A consumer whose tables live in a service schema (<c>HasDefaultSchema</c>) over a connection
  /// whose <c>search_path</c> does not include that schema — the multi-schema deployment shape.
  /// </summary>
  private sealed class SchemaScopedDbContext(DbContextOptions<SchemaScopedDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.ConfigureWhizbangInfrastructure();
      modelBuilder.HasDefaultSchema(ServiceSchema);
    }
  }

  [Test]
  public async Task GetLocalServiceIdAsync_SchemaScopedDbContext_ReadsTheIdFromTheModelSchemaAsync() {
    var expected = (Guid)TrackedGuid.NewMedo();

    await using (var setup = CreateDbContext()) {
      var conn = (NpgsqlConnection)setup.Database.GetDbConnection();
      if (conn.State != System.Data.ConnectionState.Open) {
        await conn.OpenAsync();
      }

      // The service schema's singleton row, with an id that differs from public's seeded one.
      await using var ddl = conn.CreateCommand();
      ddl.CommandText = $"""
        CREATE SCHEMA IF NOT EXISTS {ServiceSchema};
        CREATE TABLE IF NOT EXISTS {ServiceSchema}.wh_service_config (
          single_row BOOLEAN PRIMARY KEY DEFAULT TRUE CHECK (single_row),
          service_id UUID NOT NULL DEFAULT gen_random_uuid(),
          service_name TEXT NOT NULL DEFAULT 'unknown',
          created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );
        INSERT INTO {ServiceSchema}.wh_service_config (single_row, service_id)
        VALUES (TRUE, '{expected}')
        ON CONFLICT (single_row) DO UPDATE SET service_id = EXCLUDED.service_id;
        """;
      await ddl.ExecuteNonQueryAsync();
    }

    var options = new DbContextOptionsBuilder<SchemaScopedDbContext>()
      .UseNpgsql(ConnectionString)
      .Options;
    await using var schemaScoped = new SchemaScopedDbContext(options);
    var coordinator = new EFCoreWorkCoordinator<SchemaScopedDbContext>(
      schemaScoped, JsonContextRegistry.CreateCombinedOptions());

    var actual = await coordinator.GetLocalServiceIdAsync(CancellationToken.None);

    await Assert.That(actual).IsEqualTo(expected)
      .Because("the model says where wh_service_config lives; a bare table name resolves through "
             + "search_path, which throws 42P01 on a schema that is not on the path — or, as here, "
             + "quietly reads public's row and stamps every envelope with another service's identity");
  }

  [Test]
  public async Task GetLocalServiceIdAsync_PublicSchema_StillReadsTheSeededRowAsync() {
    // The default deployment shape must keep working: migration 046 seeds the singleton row.
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, JsonContextRegistry.CreateCombinedOptions());

    var actual = await coordinator.GetLocalServiceIdAsync(CancellationToken.None);

    await Assert.That(actual).IsNotEqualTo(Guid.Empty)
      .Because("public is the default schema; qualifying must not break the single-schema case");
  }
}
