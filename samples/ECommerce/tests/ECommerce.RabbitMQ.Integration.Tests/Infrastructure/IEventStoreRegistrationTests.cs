using System.Text.Json;
using ECommerce.InventoryWorker;
using ECommerce.RabbitMQ.Integration.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
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

    // Register service instance provider
    builder.Services.AddSingleton<IServiceInstanceProvider>(sp =>
      new TestServiceInstanceProvider(Guid.NewGuid(), "DiagnosticTest"));

    // IMPORTANT: Call module initializers
    ECommerce.InventoryWorker.Generated.GeneratedModelRegistration.Initialize();
    ECommerce.Contracts.Generated.WhizbangIdConverterInitializer.Initialize();

    // Create JsonSerializerOptions
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    builder.Services.AddSingleton(jsonOptions);

    // Register minimal DbContext (doesn't need real connection for this test)
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    dataSourceBuilder.ConfigureJsonOptions(jsonOptions);
    dataSourceBuilder.EnableDynamicJson();
    var dataSource = dataSourceBuilder.Build();
    builder.Services.AddSingleton(dataSource);

    builder.Services.AddDbContext<InventoryDbContext>(options => {
      options.UseNpgsql(dataSource);
    });

    // DIAGNOSTIC: Check if callback is registered BEFORE .WithDriver.Postgres
    var registrarCountBefore = GetRegistrarCount();

    // THIS IS THE KEY LINE - triggers registration
    _ = builder.Services
      .AddWhizbang()
      .WithEFCore<InventoryDbContext>()
      .WithDriver.Postgres;

    // DIAGNOSTIC: Check if callback was invoked
    var registrarCountAfter = GetRegistrarCount();

    // Build the host
    var host = builder.Build();

    // Act: Try to resolve IEventStore from a scope
    using var scope = host.Services.CreateScope();
    var eventStore = scope.ServiceProvider.GetService<IEventStore>();

    // Assert: IEventStore should be registered
    await Assert.That(eventStore).IsNotNull();
    await Assert.That(registrarCountBefore).IsGreaterThanOrEqualTo(1);
  }

  private int GetRegistrarCount() {
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
