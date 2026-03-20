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
public class InfraTestDbContext(DbContextOptions<InfraTestDbContext> options) : DbContext(options) {
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

  #region Multi-Generic ILensQuery Registration Tests

  /// <summary>
  /// Test model for multi-generic registration tests.
  /// </summary>
  public class CustomerModel {
    public required Guid Id { get; init; }
    public required string Name { get; init; }
  }

  /// <summary>
  /// Test model for multi-generic registration tests.
  /// </summary>
  public class ProductModel {
    public required Guid Id { get; init; }
    public required string Sku { get; init; }
    public required decimal Price { get; init; }
  }

  [Test]
  public async Task RegisterMultiLensQuery_TwoGeneric_RegistersILensQueryAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContextFactory<InfraTestDbContext>(options =>
      options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()));

    var tableNames = new Dictionary<Type, string> {
      { typeof(SamplePerspectiveModel), "sample_perspective" },
      { typeof(CustomerModel), "customer_perspective" }
    };

    // Act
    EFCoreInfrastructureRegistration.RegisterMultiLensQuery<InfraTestDbContext, SamplePerspectiveModel, CustomerModel>(
      services,
      tableNames);

    // Assert
    var serviceProvider = services.BuildServiceProvider();
    var query = serviceProvider.GetService<ILensQuery<SamplePerspectiveModel, CustomerModel>>();
    await Assert.That(query).IsNotNull();
  }

  [Test]
  public async Task RegisterMultiLensQuery_TwoGeneric_IsTransient_ReturnsDifferentInstancesAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContextFactory<InfraTestDbContext>(options =>
      options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()));

    var tableNames = new Dictionary<Type, string> {
      { typeof(SamplePerspectiveModel), "sample_perspective" },
      { typeof(CustomerModel), "customer_perspective" }
    };

    EFCoreInfrastructureRegistration.RegisterMultiLensQuery<InfraTestDbContext, SamplePerspectiveModel, CustomerModel>(
      services,
      tableNames);

    var serviceProvider = services.BuildServiceProvider();

    // Act
    var query1 = serviceProvider.GetRequiredService<ILensQuery<SamplePerspectiveModel, CustomerModel>>();
    var query2 = serviceProvider.GetRequiredService<ILensQuery<SamplePerspectiveModel, CustomerModel>>();

    // Assert - Transient means different instances each time
    await Assert.That(query1).IsNotSameReferenceAs(query2);
  }

  [Test]
  public async Task RegisterMultiLensQuery_TwoGeneric_CreatesCorrectTypeAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContextFactory<InfraTestDbContext>(options =>
      options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()));

    var tableNames = new Dictionary<Type, string> {
      { typeof(SamplePerspectiveModel), "sample_perspective" },
      { typeof(CustomerModel), "customer_perspective" }
    };

    EFCoreInfrastructureRegistration.RegisterMultiLensQuery<InfraTestDbContext, SamplePerspectiveModel, CustomerModel>(
      services,
      tableNames);

    // Assert
    var serviceProvider = services.BuildServiceProvider();
    var query = serviceProvider.GetService<ILensQuery<SamplePerspectiveModel, CustomerModel>>();

    await Assert.That(query).IsAssignableTo<EFCorePostgresLensQuery<SamplePerspectiveModel, CustomerModel>>();
  }

  [Test]
  public async Task RegisterMultiLensQuery_ThreeGeneric_RegistersILensQueryAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContextFactory<InfraTestDbContext>(options =>
      options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()));

    var tableNames = new Dictionary<Type, string> {
      { typeof(SamplePerspectiveModel), "sample_perspective" },
      { typeof(CustomerModel), "customer_perspective" },
      { typeof(ProductModel), "product_perspective" }
    };

    // Act
    EFCoreInfrastructureRegistration.RegisterMultiLensQuery<InfraTestDbContext, SamplePerspectiveModel, CustomerModel, ProductModel>(
      services,
      tableNames);

    // Assert
    var serviceProvider = services.BuildServiceProvider();
    var query = serviceProvider.GetService<ILensQuery<SamplePerspectiveModel, CustomerModel, ProductModel>>();
    await Assert.That(query).IsNotNull();
  }

  [Test]
  public async Task RegisterMultiLensQuery_ThreeGeneric_IsTransient_ReturnsDifferentInstancesAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContextFactory<InfraTestDbContext>(options =>
      options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()));

    var tableNames = new Dictionary<Type, string> {
      { typeof(SamplePerspectiveModel), "sample_perspective" },
      { typeof(CustomerModel), "customer_perspective" },
      { typeof(ProductModel), "product_perspective" }
    };

    EFCoreInfrastructureRegistration.RegisterMultiLensQuery<InfraTestDbContext, SamplePerspectiveModel, CustomerModel, ProductModel>(
      services,
      tableNames);

    var serviceProvider = services.BuildServiceProvider();

    // Act
    var query1 = serviceProvider.GetRequiredService<ILensQuery<SamplePerspectiveModel, CustomerModel, ProductModel>>();
    var query2 = serviceProvider.GetRequiredService<ILensQuery<SamplePerspectiveModel, CustomerModel, ProductModel>>();

    // Assert - Transient means different instances each time
    await Assert.That(query1).IsNotSameReferenceAs(query2);
  }

  [Test]
  public async Task RegisterMultiLensQuery_TwoGeneric_EachInstanceGetsDifferentDbContextAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContextFactory<InfraTestDbContext>(options =>
      options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()));

    var tableNames = new Dictionary<Type, string> {
      { typeof(SamplePerspectiveModel), "sample_perspective" },
      { typeof(CustomerModel), "customer_perspective" }
    };

    EFCoreInfrastructureRegistration.RegisterMultiLensQuery<InfraTestDbContext, SamplePerspectiveModel, CustomerModel>(
      services,
      tableNames);

    var serviceProvider = services.BuildServiceProvider();

    // Act - resolve two instances
    var query1 = serviceProvider.GetRequiredService<ILensQuery<SamplePerspectiveModel, CustomerModel>>();
    var query2 = serviceProvider.GetRequiredService<ILensQuery<SamplePerspectiveModel, CustomerModel>>();

    // Assert - use reflection to verify different DbContext instances
    // This is critical for HotChocolate parallel resolver safety
    var contextField = typeof(EFCorePostgresLensQuery<SamplePerspectiveModel, CustomerModel>)
        .GetField("_context", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    var context1 = contextField?.GetValue(query1);
    var context2 = contextField?.GetValue(query2);

    await Assert.That(context1).IsNotNull();
    await Assert.That(context2).IsNotNull();
    await Assert.That(context1).IsNotSameReferenceAs(context2);
  }

  [Test]
  public async Task RegisterMultiLensQuery_WithNullServices_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var tableNames = new Dictionary<Type, string> {
      { typeof(SamplePerspectiveModel), "sample_perspective" },
      { typeof(CustomerModel), "customer_perspective" }
    };

    // Act & Assert
    await Assert.That(() =>
        EFCoreInfrastructureRegistration.RegisterMultiLensQuery<InfraTestDbContext, SamplePerspectiveModel, CustomerModel>(
          null!,
          tableNames))
        .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task RegisterMultiLensQuery_WithNullTableNames_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act & Assert
    await Assert.That(() =>
        EFCoreInfrastructureRegistration.RegisterMultiLensQuery<InfraTestDbContext, SamplePerspectiveModel, CustomerModel>(
          services,
          null!))
        .Throws<ArgumentNullException>();
  }

  #endregion
}
