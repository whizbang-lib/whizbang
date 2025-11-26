using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Test DbContext for EFCoreDriverSelector tests.
/// </summary>
public class DriverSelectorTestDbContext : DbContext {
  public DriverSelectorTestDbContext(DbContextOptions<DriverSelectorTestDbContext> options) : base(options) { }
}

/// <summary>
/// Tests for EFCoreDriverSelector public API.
/// Since constructor and DbContextType are internal, we test them indirectly through the fluent API.
/// Target: 100% branch coverage of public surface.
/// </summary>
public class EFCoreDriverSelectorTests {
  [Test]
  public async Task WithDriver_ReturnsIDriverOptionsAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<DriverSelectorTestDbContext>();

    // Act
    var driverOptions = selector.WithDriver;

    // Assert
    await Assert.That(driverOptions).IsNotNull();
    await Assert.That(driverOptions).IsAssignableTo<IDriverOptions>();
  }

  [Test]
  public async Task WithDriver_ReturnsSelfAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<DriverSelectorTestDbContext>();

    // Act
    var driverOptions = selector.WithDriver;

    // Assert - verify it returns itself (same reference)
    await Assert.That(driverOptions).IsSameReferenceAs(selector);
  }

  [Test]
  public async Task Services_ReturnsCorrectServiceCollectionAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<DriverSelectorTestDbContext>();

    // Act & Assert
    await Assert.That(selector.Services).IsSameReferenceAs(services);
  }

  [Test]
  public async Task ImplementsIDriverOptions_InterfaceAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangPerspectiveBuilder(services);

    // Act
    var selector = builder.WithEFCore<DriverSelectorTestDbContext>();

    // Assert - verify it implements IDriverOptions
    await Assert.That(selector).IsAssignableTo<IDriverOptions>();
  }

  [Test]
  public async Task IDriverOptions_Services_ReturnsSameAsDirectPropertyAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<DriverSelectorTestDbContext>();

    // Act
    IDriverOptions driverOptions = selector;

    // Assert
    await Assert.That(driverOptions.Services).IsSameReferenceAs(selector.Services);
  }

  [Test]
  public async Task Constructor_WithNullServices_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    ServiceCollection? services = null;
    var dbContextType = typeof(DriverSelectorTestDbContext);

    // Act & Assert
    var exception = await Assert.That(() => new EFCoreDriverSelector(services!, dbContextType))
        .Throws<ArgumentNullException>();

    await Assert.That(exception.ParamName).IsEqualTo("services");
  }

  [Test]
  public async Task Constructor_WithNullDbContextType_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var services = new ServiceCollection();
    Type? dbContextType = null;

    // Act & Assert
    var exception = await Assert.That(() => new EFCoreDriverSelector(services, dbContextType!))
        .Throws<ArgumentNullException>();

    await Assert.That(exception.ParamName).IsEqualTo("dbContextType");
  }
}
