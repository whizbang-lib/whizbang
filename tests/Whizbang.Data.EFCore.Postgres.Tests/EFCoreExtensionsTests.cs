using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Test DbContext for EFCoreExtensions tests.
/// </summary>
public class SampleDbContext : DbContext {
  public SampleDbContext(DbContextOptions<SampleDbContext> options) : base(options) { }
}

/// <summary>
/// Tests for EFCoreExtensions (WithEFCore extension method).
/// Verifies the extension method properly creates EFCoreDriverSelector with correct type information.
/// Target: 100% branch coverage.
/// </summary>
public class EFCoreExtensionsTests {
  [Test]
  public async Task WithEFCore_WithValidBuilder_ReturnsEFCoreDriverSelectorAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangPerspectiveBuilder(services);

    // Act
    var selector = builder.WithEFCore<SampleDbContext>();

    // Assert
    await Assert.That(selector).IsNotNull();
    await Assert.That(selector).IsTypeOf<EFCoreDriverSelector>();
  }

  [Test]
  public async Task WithEFCore_ReturnedSelector_HasCorrectServicesAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangPerspectiveBuilder(services);

    // Act
    var selector = builder.WithEFCore<SampleDbContext>();

    // Assert
    await Assert.That(selector.Services).IsSameReferenceAs(services);
  }

  [Test]
  public async Task WithEFCore_CanChainToWithDriverAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangPerspectiveBuilder(services);

    // Act
    var selector = builder.WithEFCore<SampleDbContext>();
    var driverOptions = selector.WithDriver;

    // Assert - verify we can chain to WithDriver
    await Assert.That(driverOptions).IsNotNull();
    await Assert.That(driverOptions).IsAssignableTo<IDriverOptions>();
  }

  [Test]
  public async Task WithEFCore_MultipleContextTypes_CreatesDistinctSelectorsAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangPerspectiveBuilder(services);

    // Act
    var selector1 = builder.WithEFCore<SampleDbContext>();
    var selector2 = builder.WithEFCore<DriverSelectorTestDbContext>();

    // Assert - verify they are different instances (different context types)
    await Assert.That(selector1).IsNotSameReferenceAs(selector2);
  }

  [Test]
  public async Task WithEFCore_ReturnedSelector_ImplementsIDriverOptionsAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangPerspectiveBuilder(services);

    // Act
    var selector = builder.WithEFCore<SampleDbContext>();

    // Assert
    await Assert.That(selector).IsAssignableTo<IDriverOptions>();
  }
}
