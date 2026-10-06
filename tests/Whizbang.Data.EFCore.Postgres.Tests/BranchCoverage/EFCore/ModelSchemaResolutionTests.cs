// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lineage;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Configuration;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for the components that derive the framework schema from the consumer's EF
/// model: a model that maps no framework entity (the lookup answers null), a mapped model with no
/// schema, and a mapped model with an explicit schema all resolve to the same tables, against a
/// real database.
/// </summary>
/// <remarks>
/// Both arms of each lookup run inside this class: the CI coverage merge keeps the best per-line
/// condition count of any single shard.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresStartupAssessor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresStartupFleetStatusSource.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresApplyStackQuery.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/PhysicalColumnFillMaintenanceStep.cs</code-under-test>
[Category("Shard4")]
public class ModelSchemaResolutionTests : EFCoreTestBase {

  // With a version provider and the mapped model, the ledger is read and this binary serves; with
  // neither, the ledger is still read through the search path, and the missing provider is what
  // stands the instance down.
  [Test]
  public async Task StartupAssessor_MappedWithProvider_Serves_UnmappedWithoutProvider_StandsDownAsync() {
    await using var mapped = _provider(_ => CreateDbContext());
    var serving = await new EFCorePostgresStartupAssessor(
      mapped.GetRequiredService<IServiceScopeFactory>(), typeof(WorkCoordinationDbContext),
      new LibraryVersionProvider("999.0.0")).AssessAsync(CancellationToken.None);

    await using var unmapped = _provider(_ => _bareContext());
    var standing = await new EFCorePostgresStartupAssessor(
      unmapped.GetRequiredService<IServiceScopeFactory>(), typeof(BareContext)).AssessAsync(CancellationToken.None);

    await Assert.That(serving.Verdict).IsEqualTo(StartupVerdict.Serve)
      .Because("nothing recorded in the ledger is newer than this binary");
    await Assert.That(standing.Verdict).IsEqualTo(StartupVerdict.StandDown);
    await Assert.That(standing.Reason).Contains("no ILibraryVersionProvider")
      .Because("the ledger was read without error through the search path; the stand-down names the missing provider");
  }

  [Test]
  public async Task FleetStatus_MappedAndUnmappedModels_ReadTheSameInstanceTableAsync() {
    await using (var seed = CreateDbContext()) {
      var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(seed, JsonContextRegistry.CreateCombinedOptions());
      await coordinator.RecordHeartbeatAsync(new HeartbeatRequest((Guid)TrackedGuid.New(), "fleet-svc", "fleet-host", 7));
    }

    await using var mapped = _provider(_ => CreateDbContext());
    var fromMapped = await new EFCorePostgresStartupFleetStatusSource(
      mapped.GetRequiredService<IServiceScopeFactory>(), typeof(WorkCoordinationDbContext)).GetFleetAsync(CancellationToken.None);
    await using var unmapped = _provider(_ => _bareContext());
    var fromUnmapped = await new EFCorePostgresStartupFleetStatusSource(
      unmapped.GetRequiredService<IServiceScopeFactory>(), typeof(BareContext)).GetFleetAsync(CancellationToken.None);

    await Assert.That(fromMapped.Select(r => r.ServiceName)).Contains("fleet-svc");
    await Assert.That(fromUnmapped.Select(r => r.ServiceName)).Contains("fleet-svc")
      .Because("the unqualified tables resolve to the same public schema the mapped model reads");
  }

  [Test]
  public async Task ApplyStackQuery_MappedAndUnmappedModels_BothAnswerAsync() {
    await using var mapped = _provider(_ => CreateDbContext());
    var fromMapped = await new EFCorePostgresApplyStackQuery(
      mapped.GetRequiredService<IServiceScopeFactory>(), typeof(WorkCoordinationDbContext))
      .GetPathSignaturesAsync(new ApplyStackQueryOptions(), CancellationToken.None);
    await using var unmapped = _provider(_ => _bareContext());
    var fromUnmapped = await new EFCorePostgresApplyStackQuery(
      unmapped.GetRequiredService<IServiceScopeFactory>(), typeof(BareContext))
      .GetPathSignaturesAsync(new ApplyStackQueryOptions(), CancellationToken.None);

    await Assert.That(fromMapped).IsEmpty()
      .Because("no event has been stored, so there is no apply path to report");
    await Assert.That(fromUnmapped).IsEmpty();
  }

  // The fill step claims a window keyed by schema and time. Each model shape resolves to "public"
  // (the default when the model has no schema, or the explicit one), and the injected clock or
  // the system clock dates the window.
  [Test]
  public async Task PhysicalColumnFill_ArmedColumn_ClaimsTheWindowForEveryModelShapeAsync() {
    await using (var seed = CreateDbContext()) {
      var conn = await _openAsync(seed);
      await using var cmd = conn.CreateCommand();
      cmd.CommandText =
        "INSERT INTO wh_physical_column_fills (table_name, column_name, json_key, extraction) VALUES ('public.wh_per_gone', 'x', 'X', '(data ->> ''X'')')";
      await cmd.ExecuteNonQueryAsync();
    }
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));
    var expected = PhysicalColumnFill.ClaimKey("public", clock.GetUtcNow());

    var unmappedKey = await _fillClaimKeyAsync(_bareContext(), typeof(BareContext), clock);
    var explicitKey = await _fillClaimKeyAsync(_explicitPublicContext(), typeof(ExplicitPublicContext), clock);
    var before = DateTimeOffset.UtcNow;
    var systemKey = await _fillClaimKeyAsync(CreateDbContext(), typeof(WorkCoordinationDbContext), timeProvider: null);
    var after = DateTimeOffset.UtcNow;

    await Assert.That(unmappedKey).IsEqualTo(expected);
    await Assert.That(explicitKey).IsEqualTo(expected)
      .Because("an explicit public schema and the defaulted one claim the same window");
    await Assert.That(new[] { PhysicalColumnFill.ClaimKey("public", before), PhysicalColumnFill.ClaimKey("public", after) })
      .Contains(systemKey!)
      .Because("without an injected clock the step dates the window from the system clock");
  }

  [Test]
  public async Task PhysicalColumnFill_NullContextType_ThrowsAsync() {
    await Assert.That(() => new PhysicalColumnFillMaintenanceStep(null!)).Throws<ArgumentNullException>();
  }

  // ===== Helpers =====

  private static async Task<string?> _fillClaimKeyAsync(DbContext context, Type contextType, TimeProvider? timeProvider) {
    await using (context) {
      var claims = new RefusingClaims();
      var services = new ServiceCollection();
      services.AddSingleton(contextType, context);
      services.AddSingleton<IClaimedEmissionStore>(claims);
      await using var sp = services.BuildServiceProvider();
      await new PhysicalColumnFillMaintenanceStep(contextType, timeProvider: timeProvider).RunAsync(sp, CancellationToken.None);
      return claims.LastKey;
    }
  }

  /// <summary>A provider that resolves <typeparamref name="TContext"/> from <paramref name="factory"/>, per scope.</summary>
  private static ServiceProvider _provider<TContext>(Func<IServiceProvider, TContext> factory) where TContext : DbContext {
    var services = new ServiceCollection();
    services.AddScoped(factory);
    return services.BuildServiceProvider();
  }

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private BareContext _bareContext() =>
    new(new DbContextOptionsBuilder<BareContext>().UseNpgsql(ConnectionString).Options);

  private ExplicitPublicContext _explicitPublicContext() =>
    new(new DbContextOptionsBuilder<ExplicitPublicContext>().UseNpgsql(ConnectionString).Options);

  /// <summary>Records the claim key it was asked for and refuses it, so the step stops there.</summary>
  private sealed class RefusingClaims : IClaimedEmissionStore {
    public string? LastKey { get; private set; }

    public Task<bool> TryClaimAsync(string claimKey, Guid claimedByEventId, CancellationToken cancellationToken) {
      LastKey = claimKey;
      return Task.FromResult(false);
    }
  }

  /// <summary>A DbContext that maps none of the framework's entities.</summary>
  public sealed class BareContext(DbContextOptions<BareContext> options) : DbContext(options);

  /// <summary>A DbContext that maps the framework's entities under an explicit public schema.</summary>
  public sealed class ExplicitPublicContext(DbContextOptions<ExplicitPublicContext> options) : DbContext(options) {
    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.ConfigureWhizbangInfrastructure();
      modelBuilder.HasDefaultSchema("public");
    }
  }
}
