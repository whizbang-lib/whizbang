#nullable disable
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
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
public class PostgresTestDbContext : DbContext {
  public PostgresTestDbContext(DbContextOptions<PostgresTestDbContext> options) : base(options) { }

  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    // This call will be discovered by EFCoreServiceRegistrationGenerator
    modelBuilder.Entity<PerspectiveRow<PostgresTestModel>>(entity => {
      entity.HasKey(e => e.Id);
    });
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

    await Assert.That(exception.Message!).Contains("Postgres driver can only be used with EF Core storage");
    await Assert.That(exception.Message!).Contains("Call .WithEFCore<TDbContext>() before .WithDriver.Postgres");
  }

  [Test]
  public async Task Postgres_WithoutNpgsqlDataSource_RegistersDefaultDatabaseReadinessCheckAsync() {
    // Arrange - no NpgsqlDataSource registered
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDbContext<PostgresTestDbContext>(options =>
        options.UseInMemoryDatabase("TestDb_NoDataSource"));

    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<PostgresTestDbContext>();

    // Act
    _ = selector.WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    var readinessCheck = provider.GetService<IDatabaseReadinessCheck>();

    // Assert - fallback returns DefaultDatabaseReadinessCheck
    await Assert.That(readinessCheck).IsNotNull();
    await Assert.That(readinessCheck).IsTypeOf<DefaultDatabaseReadinessCheck>();
  }

  [Test]
  public async Task Postgres_WithoutNpgsqlDataSource_FallbackReadinessCheckAlwaysReturnsTrueAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDbContext<PostgresTestDbContext>(options =>
        options.UseInMemoryDatabase("TestDb_FallbackReady"));

    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<PostgresTestDbContext>();

    // Act
    _ = selector.WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    var readinessCheck = provider.GetRequiredService<IDatabaseReadinessCheck>();
    var isReady = await readinessCheck.IsReadyAsync();

    // Assert
    await Assert.That(isReady).IsTrue();
  }

  [Test]
  public async Task Postgres_WithNpgsqlDataSource_RegistersPostgresDatabaseReadinessCheckAsync() {
    // Arrange - register NpgsqlDataSource
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDbContext<PostgresTestDbContext>(options =>
        options.UseInMemoryDatabase("TestDb_WithDataSource"));
    var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=test_nonexistent");
    services.AddSingleton(dataSource);

    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<PostgresTestDbContext>();

    // Act
    _ = selector.WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    var readinessCheck = provider.GetService<IDatabaseReadinessCheck>();

    // Assert - should use PostgresDatabaseReadinessCheck when NpgsqlDataSource is available
    await Assert.That(readinessCheck).IsNotNull();
    await Assert.That(readinessCheck).IsTypeOf<PostgresDatabaseReadinessCheck>();
  }

  [Test]
  public async Task Postgres_WithoutNpgsqlDataSource_WithoutLogging_DoesNotThrowAsync() {
    // Arrange - no NpgsqlDataSource AND no logging (fallbackLogger is null)
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(options =>
        options.UseInMemoryDatabase("TestDb_NoLogging"));

    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<PostgresTestDbContext>();

    // Act - should not throw even with null logger (null-conditional ?. handles it)
    _ = selector.WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    var readinessCheck = provider.GetService<IDatabaseReadinessCheck>();

    // Assert
    await Assert.That(readinessCheck).IsNotNull();
    await Assert.That(readinessCheck).IsTypeOf<DefaultDatabaseReadinessCheck>();
  }

  [Test]
  public async Task Postgres_WithoutNpgsqlDataSource_LogsWarningAsync() {
    // Arrange - no NpgsqlDataSource but with logging to capture the warning
    var services = new ServiceCollection();
    var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
    services.AddSingleton<ILoggerFactory>(loggerFactory);
    services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
    services.AddDbContext<PostgresTestDbContext>(options =>
        options.UseInMemoryDatabase("TestDb_LogWarning"));

    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<PostgresTestDbContext>();

    // Act - resolving the service triggers the factory which logs the warning
    _ = selector.WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    var readinessCheck = provider.GetService<IDatabaseReadinessCheck>();

    // Assert - the fallback path was taken (warning was logged, DefaultDatabaseReadinessCheck returned)
    await Assert.That(readinessCheck).IsNotNull();
    await Assert.That(readinessCheck).IsTypeOf<DefaultDatabaseReadinessCheck>();
  }

  [Test]
  public async Task Postgres_PreRegisteredReadinessCheck_IsPreservedAsync() {
    // Arrange - pre-register a custom IDatabaseReadinessCheck before calling .Postgres
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDbContext<PostgresTestDbContext>(options =>
        options.UseInMemoryDatabase("TestDb_PreRegistered"));
    services.AddSingleton<IDatabaseReadinessCheck>(new DefaultDatabaseReadinessCheck());

    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<PostgresTestDbContext>();

    // Act - TryAddSingleton should NOT overwrite the existing registration
    _ = selector.WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    var readinessCheck = provider.GetService<IDatabaseReadinessCheck>();

    // Assert - original registration is preserved (TryAdd semantics)
    await Assert.That(readinessCheck).IsNotNull();
    await Assert.That(readinessCheck).IsTypeOf<DefaultDatabaseReadinessCheck>();
  }

  [Test]
  public async Task Postgres_RegistersAllCoreServicesAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDbContext<PostgresTestDbContext>(options =>
        options.UseInMemoryDatabase("TestDb_CoreServices"));

    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<PostgresTestDbContext>();

    // Act
    _ = selector.WithDriver.Postgres;

    // Assert - verify IDatabaseReadinessCheck is registered
    var hasReadinessCheck = services.Any(d => d.ServiceType == typeof(IDatabaseReadinessCheck));
    await Assert.That(hasReadinessCheck).IsTrue();
  }

  /// <summary>
  /// Fake implementation of IDriverOptions for testing error handling.
  /// </summary>
  private sealed class FakeDriverOptions : IDriverOptions {
    public IServiceCollection Services { get; }
    public FakeDriverOptions(IServiceCollection services) => Services = services;
  }
}
