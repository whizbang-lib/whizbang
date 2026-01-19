using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Sample perspective model for testing infrastructure registration.
/// </summary>
public class SamplePerspectiveModel {
  public required Guid Id { get; init; }
  public required string Name { get; init; }
  public long Version { get; set; }
  public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Test DbContext for EFCoreInfrastructureRegistration tests.
/// </summary>
public class InfraTestDbContext : DbContext {
  public InfraTestDbContext(DbContextOptions<InfraTestDbContext> options) : base(options) { }
}

/// <summary>
/// Tests for EFCoreInfrastructureRegistration (RegisterPerspectiveModel).
/// Verifies service registration for IPerspectiveStore and ILensQuery.
/// Target: 100% branch coverage.
/// </summary>
public class EFCoreInfrastructureRegistrationTests {
  [Test]
  public async Task RegisterPerspectiveModel_RegistersIPerspectiveStoreAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContext<InfraTestDbContext>(options =>
      options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()));

    var upsertStrategy = new InMemoryUpsertStrategy();

    // Act
    EFCoreInfrastructureRegistration.RegisterPerspectiveModel(
      services,
      typeof(InfraTestDbContext),
      typeof(SamplePerspectiveModel),
      "sample_perspective",
      upsertStrategy);

    // Assert
    var serviceProvider = services.BuildServiceProvider();
    var store = serviceProvider.GetService<IPerspectiveStore<SamplePerspectiveModel>>();
    await Assert.That(store).IsNotNull();
  }

  [Test]
  public async Task RegisterPerspectiveModel_RegistersILensQueryAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContext<InfraTestDbContext>(options =>
      options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()));

    var upsertStrategy = new InMemoryUpsertStrategy();

    // Act
    EFCoreInfrastructureRegistration.RegisterPerspectiveModel(
      services,
      typeof(InfraTestDbContext),
      typeof(SamplePerspectiveModel),
      "sample_perspective",
      upsertStrategy);

    // Assert
    var serviceProvider = services.BuildServiceProvider();
    var query = serviceProvider.GetService<ILensQuery<SamplePerspectiveModel>>();
    await Assert.That(query).IsNotNull();
  }

  [Test]
  public async Task RegisterPerspectiveModel_WithMultipleModels_RegistersBothAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContext<InfraTestDbContext>(options =>
      options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()));

    var upsertStrategy = new InMemoryUpsertStrategy();

    // Act - Register two different models
    EFCoreInfrastructureRegistration.RegisterPerspectiveModel(
      services,
      typeof(InfraTestDbContext),
      typeof(SamplePerspectiveModel),
      "sample_perspective",
      upsertStrategy);

    EFCoreInfrastructureRegistration.RegisterPerspectiveModel(
      services,
      typeof(InfraTestDbContext),
      typeof(OrderPerspective),
      "order_perspective",
      upsertStrategy);

    // Assert
    var serviceProvider = services.BuildServiceProvider();
    var store1 = serviceProvider.GetService<IPerspectiveStore<SamplePerspectiveModel>>();
    var store2 = serviceProvider.GetService<IPerspectiveStore<OrderPerspective>>();

    await Assert.That(store1).IsNotNull();
    await Assert.That(store2).IsNotNull();
  }

  [Test]
  public async Task RegisterPerspectiveModel_CreatesCorrectStoreTypeAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContext<InfraTestDbContext>(options =>
      options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()));

    var upsertStrategy = new InMemoryUpsertStrategy();

    // Act
    EFCoreInfrastructureRegistration.RegisterPerspectiveModel(
      services,
      typeof(InfraTestDbContext),
      typeof(SamplePerspectiveModel),
      "sample_perspective",
      upsertStrategy);

    // Assert
    var serviceProvider = services.BuildServiceProvider();
    var store = serviceProvider.GetService<IPerspectiveStore<SamplePerspectiveModel>>();

    await Assert.That(store).IsAssignableTo<EFCorePostgresPerspectiveStore<SamplePerspectiveModel>>();
  }

  [Test]
  public async Task RegisterPerspectiveModel_CreatesCorrectQueryTypeAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContext<InfraTestDbContext>(options =>
      options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()));

    var upsertStrategy = new InMemoryUpsertStrategy();

    // Act
    EFCoreInfrastructureRegistration.RegisterPerspectiveModel(
      services,
      typeof(InfraTestDbContext),
      typeof(SamplePerspectiveModel),
      "sample_perspective",
      upsertStrategy);

    // Assert
    var serviceProvider = services.BuildServiceProvider();
    var query = serviceProvider.GetService<ILensQuery<SamplePerspectiveModel>>();

    await Assert.That(query).IsAssignableTo<EFCorePostgresLensQuery<SamplePerspectiveModel>>();
  }
}
