#nullable disable
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

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

  /// <summary>
  /// Fake implementation of IDriverOptions for testing error handling.
  /// </summary>
  private sealed class FakeDriverOptions : IDriverOptions {
    public IServiceCollection Services { get; }
    public FakeDriverOptions(IServiceCollection services) => Services = services;
  }
}
