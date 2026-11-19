// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Test DbContext for ModelRegistrationRegistry tests.
/// </summary>
[Category("Shard2")]
public class RegistryTestDbContext(DbContextOptions<RegistryTestDbContext> options) : DbContext(options) {
}

/// <summary>
/// Tests for ModelRegistrationRegistry (RegisterModels, InvokeRegistration).
/// Verifies AOT-compatible model registration callback mechanism.
/// Target: 100% branch coverage.
/// </summary>
[NotInParallel] // Resets a process-wide registry the database tests read (#1903): runs alone, after the parallel tests.
[Category("Shard2")]
public class ModelRegistrationRegistryTests {
  private IDisposable? _restore;

  // Each test registers its own registrars. Without the reset and restore, the fakes registered here stayed in the
  // process-wide registry, and every later turnkey host ran them instead of, or beside, the generated registration.
  [Before(Test)]
  public void ResetStaticState() => _restore = ModelRegistrationRegistry.ResetForTesting();

  [After(Test)]
  public void RestoreStaticState() => _restore?.Dispose();

  [Test]
  public async Task RegisterModels_WithValidRegistrar_StoresCallbackAsync() {
    // Arrange
    var wasCalled = false;
    void registrar(IServiceCollection services, Type dbContextType, IDbUpsertStrategy strategy) {
      wasCalled = true;
    }

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
    // Arrange - the registrar list is static, append-only and process-wide, so the genuinely
    // empty-list branch cannot be reached once any test (or module initializer) has registered.
    // The closest reachable arrangement is a registrar that contributes nothing, which is what
    // this test actually pins.
    ModelRegistrationRegistry.RegisterModels((services, dbContextType, strategy) => { });

    var services = new ServiceCollection();
    var upsertStrategy = new InMemoryUpsertStrategy();

    // Act - Should not throw
    ModelRegistrationRegistry.InvokeRegistration(services, typeof(RegistryTestDbContext), upsertStrategy);

    // Assert - everything in the collection comes from the registrar; the registry adds nothing of
    // its own. A descriptor smuggled in here would be registered for every driver on every call,
    // including hosts that discovered no models at all.
    await Assert.That(services.Count).IsEqualTo(0);
  }

  [Test]
  public async Task InvokeRegistration_PassesCorrectParametersToRegistrarAsync() {
    // Arrange
    IServiceCollection? capturedServices = null;
    Type? capturedDbContextType = null;
    IDbUpsertStrategy? capturedStrategy = null;

    void registrar(IServiceCollection services, Type dbContextType, IDbUpsertStrategy strategy) {
      capturedServices = services;
      capturedDbContextType = dbContextType;
      capturedStrategy = strategy;
    }

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

  private sealed record OrderModel;
  private sealed record InvoiceModel;
  private sealed class OtherDbContext;

  private static Action<IServiceCollection, Type, IDbUpsertStrategy> _recording(List<string> ran, string name) =>
    (_, _, _) => ran.Add(name);

  private static RegistrationCollision[] _reported(IServiceCollection services) {
    using var provider = services.BuildServiceProvider();
    return [.. provider.GetServices<IRegistrationCollisionSource>().SelectMany(source => source.GetCollisions())];
  }

  [Test]
  public async Task InvokeRegistration_EveryAssemblysRegistrar_RunsAsync() {
    var ran = new List<string>();
    ModelRegistrationRegistry.RegisterModels("Orders", [(typeof(RegistryTestDbContext), typeof(OrderModel))], _recording(ran, "Orders"));
    ModelRegistrationRegistry.RegisterModels("Billing", [(typeof(RegistryTestDbContext), typeof(InvoiceModel))], _recording(ran, "Billing"));

    ModelRegistrationRegistry.InvokeRegistration(new ServiceCollection(), typeof(RegistryTestDbContext), new InMemoryUpsertStrategy());

    await Assert.That(ran.SequenceEqual(["Orders", "Billing"])).IsTrue()
      .Because("every assembly's models are registered, in the order the assemblies registered, so the latest "
             + $"assembly's registration of a model is the one the container serves (#2201); ran: {string.Join(", ", ran)}");
  }

  [Test]
  public async Task RegisterModels_TheSameAssemblyAgain_ReplacesOnlyItsOwnEntryAsync() {
    var ran = new List<string>();
    ModelRegistrationRegistry.RegisterModels("Orders", [], _recording(ran, "Orders v1"));
    ModelRegistrationRegistry.RegisterModels("Billing", [], _recording(ran, "Billing"));
    ModelRegistrationRegistry.RegisterModels("Orders", [], _recording(ran, "Orders v2"));

    ModelRegistrationRegistry.InvokeRegistration(new ServiceCollection(), typeof(RegistryTestDbContext), new InMemoryUpsertStrategy());

    await Assert.That(ran.SequenceEqual(["Orders v2", "Billing"])).IsTrue()
      .Because("a re-registration (hot reload) replaces its own assembly's entry in place, never another assembly's; "
             + $"ran: {string.Join(", ", ran)}");
  }

  [Test]
  public async Task InvokeRegistration_TwiceOnOneCollection_RunsEachRegistrarOnceAsync() {
    var ran = new List<string>();
    ModelRegistrationRegistry.RegisterModels("Orders", [], _recording(ran, "Orders"));
    ModelRegistrationRegistry.RegisterModels(_recording(ran, "Unnamed"));
    var services = new ServiceCollection();

    ModelRegistrationRegistry.InvokeRegistration(services, typeof(RegistryTestDbContext), new InMemoryUpsertStrategy());
    ModelRegistrationRegistry.InvokeRegistration(services, typeof(RegistryTestDbContext), new InMemoryUpsertStrategy());

    await Assert.That(ran.SequenceEqual(["Orders", "Unnamed"])).IsTrue();
  }

  [Test]
  public async Task InvokeRegistration_AModelTwoAssembliesRegister_IsReportedAsync() {
    ModelRegistrationRegistry.RegisterModels(static (_, _, _) => { });
    ModelRegistrationRegistry.RegisterModels("Orders", [
      (typeof(RegistryTestDbContext), typeof(OrderModel)),
      (typeof(OtherDbContext), typeof(InvoiceModel)),
    ], static (_, _, _) => { });
    ModelRegistrationRegistry.RegisterModels("Host", [
      (typeof(RegistryTestDbContext), typeof(OrderModel)),
      (typeof(RegistryTestDbContext), typeof(InvoiceModel)),
    ], static (_, _, _) => { });
    var services = new ServiceCollection();

    ModelRegistrationRegistry.InvokeRegistration(services, typeof(RegistryTestDbContext), new InMemoryUpsertStrategy());
    ModelRegistrationRegistry.InvokeRegistration(services, typeof(RegistryTestDbContext), new InMemoryUpsertStrategy());

    var reported = _reported(services);
    await Assert.That(reported.Length).IsEqualTo(1)
      .Because("the one model both assemblies register for this DbContext is reported, once however often the "
             + "driver registers; the model one assembly registers for another DbContext is not");
    await Assert.That(reported[0]).IsEqualTo(new RegistrationCollision(
      "Perspective model",
      $"{typeof(OrderModel).FullName} in {typeof(RegistryTestDbContext).FullName}",
      "Host", "Orders"));
  }

  [Test]
  public async Task InvokeRegistration_NoModelTwoAssembliesShare_ReportsNothingAsync() {
    ModelRegistrationRegistry.RegisterModels("Orders", [(typeof(RegistryTestDbContext), typeof(OrderModel))], static (_, _, _) => { });
    ModelRegistrationRegistry.RegisterModels("Billing", [(typeof(RegistryTestDbContext), typeof(InvoiceModel))], static (_, _, _) => { });
    var services = new ServiceCollection();

    ModelRegistrationRegistry.InvokeRegistration(services, typeof(RegistryTestDbContext), new InMemoryUpsertStrategy());

    await Assert.That(_reported(services)).IsEmpty();
  }

  [Test]
  public async Task RegisterModels_NullArguments_ThrowAsync() {
    await Assert.That(() => ModelRegistrationRegistry.RegisterModels(null!, [], static (_, _, _) => { }))
      .Throws<ArgumentNullException>();
    await Assert.That(() => ModelRegistrationRegistry.RegisterModels("Orders", null!, static (_, _, _) => { }))
      .Throws<ArgumentNullException>();
  }
}
