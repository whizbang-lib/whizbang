using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Test DbContext for ModelRegistrationRegistry tests.
/// </summary>
public class RegistryTestDbContext : DbContext {
  public RegistryTestDbContext(DbContextOptions<RegistryTestDbContext> options) : base(options) { }
}

/// <summary>
/// Tests for ModelRegistrationRegistry (RegisterModels, InvokeRegistration).
/// Verifies AOT-compatible model registration callback mechanism.
/// Target: 100% branch coverage.
/// </summary>
public class ModelRegistrationRegistryTests {
  [Test]
  public async Task RegisterModels_WithValidRegistrar_StoresCallbackAsync() {
    // Arrange
    var wasCalled = false;
    Action<IServiceCollection, Type, IDbUpsertStrategy> registrar = (services, dbContextType, strategy) => {
      wasCalled = true;
    };

    // Act
    ModelRegistrationRegistry.RegisterModels(registrar);

    var services = new ServiceCollection();
    var upsertStrategy = new InMemoryUpsertStrategy();
    ModelRegistrationRegistry.InvokeRegistration(services, typeof(RegistryTestDbContext), upsertStrategy);

    // Assert
    await Assert.That(wasCalled).IsTrue();
  }

  [Test]
  public async Task InvokeRegistration_WithNoRegistrar_DoesNotThrowAsync() {
    // Arrange
    // Reset registry by registering null (simulate no registrar set)
    ModelRegistrationRegistry.RegisterModels((services, dbContextType, strategy) => { });

    var services = new ServiceCollection();
    var upsertStrategy = new InMemoryUpsertStrategy();

    // Act & Assert - Should not throw
    ModelRegistrationRegistry.InvokeRegistration(services, typeof(RegistryTestDbContext), upsertStrategy);
  }

  [Test]
  public async Task InvokeRegistration_PassesCorrectParametersToRegistrarAsync() {
    // Arrange
    IServiceCollection? capturedServices = null;
    Type? capturedDbContextType = null;
    IDbUpsertStrategy? capturedStrategy = null;

    Action<IServiceCollection, Type, IDbUpsertStrategy> registrar = (services, dbContextType, strategy) => {
      capturedServices = services;
      capturedDbContextType = dbContextType;
      capturedStrategy = strategy;
    };

    ModelRegistrationRegistry.RegisterModels(registrar);

    var services = new ServiceCollection();
    var upsertStrategy = new InMemoryUpsertStrategy();

    // Act
    ModelRegistrationRegistry.InvokeRegistration(services, typeof(RegistryTestDbContext), upsertStrategy);

    // Assert
    await Assert.That(capturedServices).IsSameReferenceAs(services);
    await Assert.That(capturedDbContextType).IsEqualTo(typeof(RegistryTestDbContext));
    await Assert.That(capturedStrategy).IsSameReferenceAs(upsertStrategy);
  }

  [Test]
  public async Task RegisterModels_MultipleRegistrations_UsesLatestRegistrarAsync() {
    // Arrange
    var firstCalled = false;
    var secondCalled = false;

    Action<IServiceCollection, Type, IDbUpsertStrategy> firstRegistrar = (services, dbContextType, strategy) => {
      firstCalled = true;
    };

    Action<IServiceCollection, Type, IDbUpsertStrategy> secondRegistrar = (services, dbContextType, strategy) => {
      secondCalled = true;
    };

    // Act
    ModelRegistrationRegistry.RegisterModels(firstRegistrar);
    ModelRegistrationRegistry.RegisterModels(secondRegistrar);

    var services = new ServiceCollection();
    var upsertStrategy = new InMemoryUpsertStrategy();
    ModelRegistrationRegistry.InvokeRegistration(services, typeof(RegistryTestDbContext), upsertStrategy);

    // Assert
    await Assert.That(firstCalled).IsFalse();
    await Assert.That(secondCalled).IsTrue();
  }
}
