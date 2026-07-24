using System.Text.Json;
using ECommerce.InventoryWorker;
using ECommerce.RabbitMQ.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres;
using Whizbang.Transports.RabbitMQ;

namespace ECommerce.RabbitMQ.Integration.Tests.Infrastructure;

/// <summary>
/// Diagnostic test to verify IEventStore is properly registered in test hosts
/// </summary>
public class IEventStoreRegistrationTests {
  [Test]
  public async Task InventoryHost_RegistersIEventStore_SuccessfullyAsync() {
    // Arrange: Create a minimal host like the fixture does
    var builder = Host.CreateApplicationBuilder();

    // Use in-memory connection for this diagnostic test
    var connectionString = "Host=localhost;Port=5432;Database=test;Username=test;Password=test";

    // Add connection string to configuration (required by generated turnkey code)
    // The generated code derives "inventory-db" from "InventoryDbContext"
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:inventory-db"] = connectionString
    });

    // Register service instance provider
    builder.Services.AddSingleton<IServiceInstanceProvider>(sp =>
      new TestServiceInstanceProvider(Guid.NewGuid(), "DiagnosticTest"));

    // IMPORTANT: Call module initializers
    ECommerce.InventoryWorker.Generated.GeneratedModelRegistration.Initialize();
    ECommerce.Contracts.Generated.WhizbangIdConverterInitializer.Initialize();

    // Create JsonSerializerOptions
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    builder.Services.AddSingleton(jsonOptions);

    // Turnkey registration (via .WithEFCore<T>().WithDriver.Postgres below) handles:
    // - NpgsqlDataSource creation with ConfigureJsonOptions + EnableDynamicJson
    // - AddDbContext<InventoryDbContext> with UseNpgsql
    // - IDbContextFactory<InventoryDbContext> singleton registration
    // Connection string is provided via config above

    // DIAGNOSTIC: Check if callback is registered BEFORE .WithDriver.Postgres
    var registrarCountBefore = _getRegistrarCount();

    // CRITICAL: Clear the global Dispatcher callback before calling AddWhizbang().
    // The ECommerce.Integration.TestUtilities assembly's module initializer overwrites
    // ServiceRegistrationCallbacks.Dispatcher with a callback that registers
    // DistributeStageTestReceptor (which has unresolvable constructor dependencies).
    ServiceRegistrationCallbacks.Dispatcher = null;

    // THIS IS THE KEY LINE - triggers registration
    _ = builder.Services
      .AddWhizbang()
      .WithEFCore<InventoryDbContext>()
      .WithDriver.Postgres;

    // DIAGNOSTIC: Check if callback was invoked
    var registrarCountAfter = _getRegistrarCount();

    // Build the host
    var host = builder.Build();

    // Act: Try to resolve IEventStore from a scope
    using var scope = host.Services.CreateScope();
    var eventStore = scope.ServiceProvider.GetService<IEventStore>();

    // Assert: IEventStore should be registered
    await Assert.That(eventStore).IsNotNull();
    await Assert.That(registrarCountBefore).IsGreaterThanOrEqualTo(1);
  }

  private int _getRegistrarCount() {
    // Use reflection to access private static field for diagnostic purposes
    var registryType = typeof(ModelRegistrationRegistry);
    var registrarsField = registryType.GetField("_registrars",
      System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    if (registrarsField != null) {
      var registrars = registrarsField.GetValue(null) as System.Collections.IList;
      return registrars?.Count ?? -1;
    }
    return -1;
  }
}
