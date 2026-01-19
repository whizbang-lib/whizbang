using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Test DbContext for WhizbangBuilderExtensions tests.
/// </summary>
public class WhizbangBuilderExtensionsTestDbContext : DbContext {
  public WhizbangBuilderExtensionsTestDbContext(DbContextOptions<WhizbangBuilderExtensionsTestDbContext> options) : base(options) { }

  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    modelBuilder.Entity<PerspectiveRow<WhizbangBuilderExtensionsTestModel>>(entity => {
      entity.HasKey(e => e.Id);
    });
  }
}

/// <summary>
/// Test model for WithEFCore tests.
/// </summary>
public class WhizbangBuilderExtensionsTestModel {
  public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Tests for WhizbangBuilder extension methods (WithEFCore).
/// Verifies the unified .AddWhizbang().WithEFCore() API.
/// Target: 100% branch coverage.
/// </summary>
public class WhizbangBuilderExtensionsTests {
  [Test]
  public async Task WithEFCore_WithValidBuilder_ReturnsEFCoreDriverSelectorAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = services.AddWhizbang();

    // Act
    var selector = builder.WithEFCore<WhizbangBuilderExtensionsTestDbContext>();

    // Assert
    await Assert.That(selector).IsNotNull();
    await Assert.That(selector).IsTypeOf<EFCoreDriverSelector>();
  }

  [Test]
  public async Task WithEFCore_ReturnedSelector_HasCorrectServicesAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = services.AddWhizbang();

    // Act
    var selector = builder.WithEFCore<WhizbangBuilderExtensionsTestDbContext>();

    // Assert
    await Assert.That(selector.Services).IsSameReferenceAs(services);
  }

  [Test]
  public async Task WithEFCore_CanChainToWithDriverAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = services.AddWhizbang();

    // Act
    var selector = builder.WithEFCore<WhizbangBuilderExtensionsTestDbContext>();
    var driverOptions = selector.WithDriver;

    // Assert - verify we can chain to WithDriver
    await Assert.That(driverOptions).IsNotNull();
    await Assert.That(driverOptions).IsAssignableTo<IDriverOptions>();
  }

  [Test]
  public async Task AddWhizbang_WithEFCore_InMemory_FullChainAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContext<WhizbangBuilderExtensionsTestDbContext>(options =>
        options.UseInMemoryDatabase("TestDb"));

    // Act - full unified API chain
    _ = services
        .AddWhizbang()
        .WithEFCore<WhizbangBuilderExtensionsTestDbContext>()
        .WithDriver.InMemory;

    // Assert - verify the chain completes successfully
    await Assert.That(services.Count).IsGreaterThan(0);
  }

  [Test]
  public async Task AddWhizbang_WithEFCore_Postgres_FullChainAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContext<WhizbangBuilderExtensionsTestDbContext>(options =>
        options.UseInMemoryDatabase("TestDb"));

    // Act - full unified API chain with Postgres driver
    _ = services
        .AddWhizbang()
        .WithEFCore<WhizbangBuilderExtensionsTestDbContext>()
        .WithDriver.Postgres;

    // Assert - verify the chain completes successfully
    await Assert.That(services.Count).IsGreaterThan(0);
  }
}
