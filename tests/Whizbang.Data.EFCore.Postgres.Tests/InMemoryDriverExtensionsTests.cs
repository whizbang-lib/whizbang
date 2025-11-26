using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Test model for InMemoryDriverExtensions tests.
/// </summary>
public class InMemoryTestModel {
  public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Test DbContext with ConfigurePerspectiveRow for auto-discovery.
/// </summary>
public class InMemoryTestDbContext : DbContext {
  public InMemoryTestDbContext(DbContextOptions<InMemoryTestDbContext> options) : base(options) { }

  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    // This call will be discovered by EFCoreServiceRegistrationGenerator
    modelBuilder.Entity<PerspectiveRow<InMemoryTestModel>>(entity => {
      entity.HasKey(e => e.Id);
    });
  }
}

/// <summary>
/// Tests for InMemoryDriverExtensions (.InMemory property).
/// Verifies driver registration, service configuration, and error handling.
/// Target: 100% branch coverage.
/// </summary>
public class InMemoryDriverExtensionsTests {
  [Test]
  public async Task InMemory_WithValidEFCoreSelector_ReturnsWhizbangPerspectiveBuilderAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContext<InMemoryTestDbContext>(options =>
        options.UseInMemoryDatabase("TestDb"));

    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<InMemoryTestDbContext>();

    // Act
    var result = selector.WithDriver.InMemory;

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsTypeOf<WhizbangPerspectiveBuilder>();
  }

  [Test]
  public async Task InMemory_ReturnedBuilder_HasSameServicesAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContext<InMemoryTestDbContext>(options =>
        options.UseInMemoryDatabase("TestDb"));

    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<InMemoryTestDbContext>();

    // Act
    var result = selector.WithDriver.InMemory;

    // Assert
    await Assert.That(result.Services).IsSameReferenceAs(services);
  }

  [Test]
  public async Task InMemory_WithNonEFCoreDriverOptions_ThrowsInvalidOperationExceptionAsync() {
    // Arrange - create a fake IDriverOptions that isn't EFCoreDriverSelector
    var services = new ServiceCollection();
    IDriverOptions fakeOptions = new FakeDriverOptions(services);

    // Act & Assert
    var exception = await Assert.That(() => fakeOptions.InMemory)
        .Throws<InvalidOperationException>();

    await Assert.That(exception.Message).Contains("InMemory driver can only be used with EF Core storage");
    await Assert.That(exception.Message).Contains("Call .WithEFCore<TDbContext>() before .WithDriver.InMemory");
  }

  /// <summary>
  /// Fake implementation of IDriverOptions for testing error handling.
  /// </summary>
  private class FakeDriverOptions : IDriverOptions {
    public IServiceCollection Services { get; }
    public FakeDriverOptions(IServiceCollection services) => Services = services;
  }
}
