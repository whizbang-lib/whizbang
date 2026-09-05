using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The Postgres driver registers its turnkey services as factories, so the construction sits
/// inside a lambda that runs on first resolution rather than at registration. Counting or
/// asserting the descriptors never executes them, which means a service whose constructor gains
/// a dependency nobody registered stays green in every registration test and throws the first
/// time an application asks for it -- during startup, or later, on the first request that needs
/// it.
/// </summary>
/// <remarks>
/// No database is required. NpgsqlDataSourceBuilder.Build() does not connect, and neither
/// UseNpgsql nor any of these factories opens a connection; they only capture the data source.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/PostgresDriverExtensions.cs</code-under-test>
[Category("Shard3")]
public class PostgresDriverRegistrationTests {

  private const string OFFLINE_CONNECTION_STRING =
    "Host=localhost;Port=5432;Database=whizbang_registration_probe;Username=probe;Password=probe";

  [Test]
  public async Task Postgres_EveryServiceItRegistersCanBeResolvedAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddWhizbang();

    await using var dataSource = new NpgsqlDataSourceBuilder(OFFLINE_CONNECTION_STRING).Build();
    services.AddSingleton(dataSource);
    services.AddDbContext<DriverSelectorTestDbContext>(o => o.UseNpgsql(dataSource));

    var beforeDriver = services.Count;
    _ = new WhizbangPerspectiveBuilder(services)
      .WithEFCore<DriverSelectorTestDbContext>()
      .WithDriver.Postgres;

    var registered = services.Skip(beforeDriver).ToList();

    await using var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();

    var failures = new List<string>();

    foreach (var descriptor in registered) {
      if (descriptor.ImplementationInstance is not null) {
        continue;
      }

      // The driver calls PerspectiveRunnerCallbackRegistry.InvokeRegistration, which registers
      // the *consumer's* generated perspective runners. Those need the consumer's own event
      // store and the rest of its graph; requiring them here would make a driver test depend on
      // whatever the test assembly happens to declare. Only the driver's own registrations are
      // this test's subject.
      if (descriptor.ServiceType.Assembly == typeof(PostgresDriverRegistrationTests).Assembly) {
        continue;
      }

      var name = descriptor.ServiceType.Name;

      try {
        // Resolving through the scope covers singleton and scoped alike. The factory is what
        // matters: it is the code that never runs until something asks.
        var resolved = scope.ServiceProvider.GetService(descriptor.ServiceType);

        if (resolved is null) {
          failures.Add($"{name} resolved to null");
        }
      } catch (Exception ex) {
        failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
      }
    }

    // Named explicitly so the sweep above cannot pass by having skipped everything: these are
    // the turnkey services the driver exists to provide, and each is built by its own factory.
    await Assert.That(scope.ServiceProvider.GetService<IMessageTypeRegistryPopulator>()).IsNotNull();
    await Assert.That(scope.ServiceProvider.GetService<IPerspectiveSnapshotStore>()).IsNotNull();

    await Assert.That(registered).IsNotEmpty()
      .Because("the assertion below is vacuous if the driver registered nothing");
    await Assert.That(failures).IsEmpty()
      .Because("a turnkey service that cannot be built from the container the driver registered "
             + "it into fails at startup, not here");
  }
}
