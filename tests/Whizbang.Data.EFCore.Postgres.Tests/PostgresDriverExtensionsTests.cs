#nullable disable
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Dispatch;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Test model for PostgresDriverExtensions tests.
/// </summary>
public class PostgresTestModel {
  public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Test DbContext with ConfigurePerspectiveRow for auto-discovery.
/// </summary>
public class PostgresTestDbContext(DbContextOptions<PostgresTestDbContext> options) : DbContext(options) {
  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    // This call will be discovered by EFCoreServiceRegistrationGenerator
    modelBuilder.Entity<PerspectiveRow<PostgresTestModel>>(entity => entity.HasKey(e => e.Id));
  }
}

/// <summary>
/// Tests for PostgresDriverExtensions (.Postgres property).
/// Verifies driver registration, service configuration, and error handling.
/// Target: 100% branch coverage.
/// </summary>
public class PostgresDriverExtensionsTests {
  [Test]
  public async Task Postgres_WithValidEFCoreSelector_ReturnsWhizbangPerspectiveBuilderAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(options =>
        options.UseInMemoryDatabase("TestDb"));

    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<PostgresTestDbContext>();

    // Act
    var result = selector.WithDriver.Postgres;

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsTypeOf<WhizbangPerspectiveBuilder>();
  }

  [Test]
  public async Task Postgres_ReturnedBuilder_HasSameServicesAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(options =>
        options.UseInMemoryDatabase("TestDb"));

    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<PostgresTestDbContext>();

    // Act
    var result = selector.WithDriver.Postgres;

    // Assert
    await Assert.That(result.Services).IsSameReferenceAs(services);
  }

  [Test]
  public async Task Postgres_WithNonEFCoreDriverOptions_ThrowsInvalidOperationExceptionAsync() {
    // Arrange - create a fake IDriverOptions that isn't EFCoreDriverSelector
    var services = new ServiceCollection();
    IDriverOptions fakeOptions = new FakeDriverOptions(services);

    // Act & Assert
    var exception = await Assert.That(() => fakeOptions.Postgres)
        .Throws<InvalidOperationException>();

    await Assert.That(exception.Message).Contains("Postgres driver can only be used with EF Core storage");
    await Assert.That(exception.Message).Contains("Call .WithEFCore<TDbContext>() before .WithDriver.Postgres");
  }

  // Removed Postgres_*ReadinessCheck* tests — Phase H decoupled startup gating from
  // IDatabaseReadinessCheck. Workers now wait on ISchemaReadyGate. The Postgres driver
  // extensions no longer register IDatabaseReadinessCheck.

  [Test]
  public async Task Postgres_RegistersDbContextNotificationConnectionStringFallbackAsync() {
    // Locks the DI wiring that lets PgWorkNotificationListener +
    // PgCommitOrderStamperWorker reach the DbContext-backed fallback when
    // Whizbang:Database is unconfigured. The connection-string round-trip itself is
    // covered by DbContextNotificationConnectionStringFallbackTests — here we just
    // verify the singleton lands in DI with the right concrete type.
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(o => o.UseInMemoryDatabase("TestDb"));

    var builder = new WhizbangPerspectiveBuilder(services);
    _ = builder.WithEFCore<PostgresTestDbContext>().WithDriver.Postgres;

    using var sp = services.BuildServiceProvider();
    var fallback = sp.GetService<INotificationConnectionStringFallback>();

    await Assert.That(fallback).IsNotNull();
    await Assert.That(fallback).IsTypeOf<DbContextNotificationConnectionStringFallback>();
  }

  // ── Turnkey IClaimedEmissionStore registration ────────────────────────────────

  [Test]
  public async Task Postgres_RegistersIClaimedEmissionStore_ScopedAsync() {
    // The Postgres driver routes saga completion through PublishOnceAsync, which requires
    // IClaimedEmissionStore. Before this turnkey wiring, every Postgres consumer had to
    // register EFCoreClaimedEmissionStore manually — a consumer had to add the registration in
    // both Program.cs and the integration fixture to avoid PublishOnceAsync throwing at
    // saga completion (production saga 019f000e regression). With this in place, .WithDriver.Postgres
    // is genuinely turnkey for the dispatcher's exactly-once primitive.
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(o => o.UseInMemoryDatabase("TestDb"));

    var builder = new WhizbangPerspectiveBuilder(services);
    _ = builder.WithEFCore<PostgresTestDbContext>().WithDriver.Postgres;

    using var sp = services.BuildServiceProvider();
    using var scope = sp.CreateScope();
    var store = scope.ServiceProvider.GetService<IClaimedEmissionStore>();

    await Assert.That(store).IsNotNull()
      .Because(".WithDriver.Postgres MUST turnkey-register IClaimedEmissionStore so consumers " +
               "don't have to wire it manually; PublishOnceAsync throws without it.");
    await Assert.That(store).IsTypeOf<EFCoreClaimedEmissionStore>();

    var descriptor = services.First(sd => sd.ServiceType == typeof(IClaimedEmissionStore));
    await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Scoped)
      .Because("EFCoreClaimedEmissionStore holds a DbContext; lifetime must match the DbContext " +
               "scope so the same transaction sees the claim row.");
  }

  [Test]
  public async Task Postgres_DoesNotOverrideExistingClaimedEmissionStore_Async() {
    // Locks the TryAdd semantics: consumers who already supply an IClaimedEmissionStore
    // (e.g. a Dapper-backed override) keep their implementation when .WithDriver.Postgres
    // runs. Otherwise upgrading Whizbang versions would silently replace consumer overrides.
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(o => o.UseInMemoryDatabase("TestDb"));
    services.AddScoped<IClaimedEmissionStore, NoOpClaimedEmissionStore>();

    var builder = new WhizbangPerspectiveBuilder(services);
    _ = builder.WithEFCore<PostgresTestDbContext>().WithDriver.Postgres;

    using var sp = services.BuildServiceProvider();
    using var scope = sp.CreateScope();
    var store = scope.ServiceProvider.GetService<IClaimedEmissionStore>();

    await Assert.That(store).IsTypeOf<NoOpClaimedEmissionStore>()
      .Because("TryAdd semantics: consumer-supplied override MUST win over the driver's default.");
  }

  /// <summary>
  /// Fake implementation of IDriverOptions for testing error handling.
  /// </summary>
  private sealed class FakeDriverOptions(IServiceCollection services) : IDriverOptions {
    public IServiceCollection Services { get; } = services;
  }

  private sealed class NoOpClaimedEmissionStore : IClaimedEmissionStore {
    public Task<bool> TryClaimAsync(string claimKey, Guid claimedByEventId, CancellationToken cancellationToken)
      => Task.FromResult(true);
  }
}
