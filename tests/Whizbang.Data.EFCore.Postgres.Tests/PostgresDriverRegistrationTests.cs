// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;

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
  public async Task Postgres_CarriesMaxInFlightCommandsIntoTheWorkCoordinatorGateAsync() {
    // PostgresOptions.MaxInFlightCommands is the documented cap on concurrent coordinator calls; it
    // used to reach nothing because the worker pipeline built its gate with a literal 50.
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.Configure<PostgresOptions>(o => o.MaxInFlightCommands = 7);
    services.AddWhizbang();
    await using var dataSource = new NpgsqlDataSourceBuilder(OFFLINE_CONNECTION_STRING).Build();
    services.AddSingleton(dataSource);
    services.AddDbContext<DriverSelectorTestDbContext>(o => o.UseNpgsql(dataSource));
    _ = new WhizbangPerspectiveBuilder(services)
      .WithEFCore<DriverSelectorTestDbContext>()
      .WithDriver.Postgres;

    await using var provider = services.BuildServiceProvider();
    var gate = provider.GetRequiredService<WorkCoordinatorGate>();

    await Assert.That(gate.MaxConcurrent).IsEqualTo(7)
      .Because("the documented option is the one that takes effect, whichever registration ran first");
  }

  [Test]
  public async Task Postgres_ScopesTheContextsSchemaRequirementsToItsDatabaseAsync() {
    // A context's schema requires its extensions on the database it uses: the scope its capabilities are probed
    // under, the connection string name.
    var services = new ServiceCollection();
    services.AddLogging();
    await using var dataSource = new NpgsqlDataSourceBuilder(OFFLINE_CONNECTION_STRING).Build();
    services.AddDbContext<DriverSelectorTestDbContext>(o => o.UseNpgsql(dataSource));
    _ = new WhizbangPerspectiveBuilder(services)
      .WithEFCore<DriverSelectorTestDbContext>("orders-db")
      .WithDriver.Postgres;

    await using var provider = services.BuildServiceProvider();
    var scoped = provider.GetServices<Whizbang.Core.Capabilities.ICapabilityRequirementSource>()
      .OfType<Whizbang.Core.Capabilities.ScopedCapabilityRequirementSource>()
      .ToList();

    await Assert.That(scoped.Select(s => s.Scope)).Contains("orders-db");
  }

  [Test]
  public async Task Postgres_RegistersAnInstanceIdentity_WhenTheHostHasNoneAsync() {
    // The schema initializer registers this instance and the managed-object reconcile records what it declares
    // under its id; neither can when the driver is composed without the core registration.
    var services = new ServiceCollection();
    await using var dataSource = new NpgsqlDataSourceBuilder(OFFLINE_CONNECTION_STRING).Build();
    services.AddDbContext<DriverSelectorTestDbContext>(o => o.UseNpgsql(dataSource));
    _ = new WhizbangPerspectiveBuilder(services)
      .WithEFCore<DriverSelectorTestDbContext>()
      .WithDriver.Postgres;

    await Assert.That(services.Any(d => d.ServiceType == typeof(Whizbang.Core.Observability.IServiceInstanceProvider))).IsTrue();
  }

  [Test]
  public async Task Postgres_ForAConsumerRegisteredContext_RegistersItsManagedObjectManifestAsync() {
    // The consumer registered the context itself, so the generated turnkey registration is skipped; the periodic
    // managed-object reconcile still needs the context's manifest.
    var services = new ServiceCollection();
    await using var dataSource = new NpgsqlDataSourceBuilder(OFFLINE_CONNECTION_STRING).Build();
    services.AddDbContext<DocumentIndexesDbContext>(o => o.UseNpgsql(dataSource));
    _ = new WhizbangPerspectiveBuilder(services)
      .WithEFCore<DocumentIndexesDbContext>()
      .WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();

    var manifest = provider.GetKeyedService<Whizbang.Data.Postgres.Schema.ManagedSchemaManifest>(typeof(DocumentIndexesDbContext));

    await Assert.That(manifest).IsNotNull();
    await Assert.That(manifest!.Schema).IsEqualTo("public");
  }

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
    await Assert.That(scope.ServiceProvider.GetService<Whizbang.Core.Observability.IAdvisoryLedger>())
      .IsNotNull()
      .Because("without it the advisory keeps its findings in process memory, so every replica "
             + "reports the same table and every restart starts the count again -- which looks "
             + "exactly like a deployment that chose that, and is the one failure this registration "
             + "exists to prevent");

    await Assert.That(registered).IsNotEmpty()
      .Because("the assertion below is vacuous if the driver registered nothing");
    await Assert.That(failures).IsEmpty()
      .Because("a turnkey service that cannot be built from the container the driver registered "
             + "it into fails at startup, not here");
  }

  /// <summary>
  /// The three surfaces the driver exists to hand a host that wired nothing: the checkpoint
  /// completer a rebuild needs to persist its cursors (without it a rebuild reprojects every row
  /// and leaves wh_perspective_cursors wherever live processing last wrote), the apply-stack query
  /// the lineage endpoints answer from, and the fleet source the startup status report's fleet
  /// section reads. Each is built by its own factory over the CONSUMER's DbContext type, and each
  /// factory only runs on first resolution — so the registration has to be resolved, and resolved
  /// to the Postgres implementation, for any of that to be true.
  /// </summary>
  [Test]
  public async Task Postgres_ResolvesTheRebuildAndStatusSurfacesOverTheConsumersDbContextAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddWhizbang();

    await using var dataSource = new NpgsqlDataSourceBuilder(OFFLINE_CONNECTION_STRING).Build();
    services.AddSingleton(dataSource);
    services.AddDbContext<DriverSelectorTestDbContext>(o => o.UseNpgsql(dataSource));
    _ = new WhizbangPerspectiveBuilder(services)
      .WithEFCore<DriverSelectorTestDbContext>()
      .WithDriver.Postgres;

    await using var provider = services.BuildServiceProvider();
    using var first = provider.CreateScope();
    using var second = provider.CreateScope();

    var completer = first.ServiceProvider.GetService<IPerspectiveCheckpointCompleter>();
    var applyStack = first.ServiceProvider.GetService<Whizbang.Core.Lineage.IApplyStackQuery>();
    var fleet = first.ServiceProvider.GetService<Whizbang.Core.Startup.IStartupFleetStatusSource>();

    await Assert.That(completer).IsTypeOf<EFCorePostgresPerspectiveCheckpointCompleter>()
      .Because("a rebuild resolves this interface to write its cursor checkpoints; anything else leaves them unwritten");
    await Assert.That(applyStack).IsTypeOf<EFCorePostgresApplyStackQuery>()
      .Because("the lineage surfaces answer from whatever is registered here");
    await Assert.That(fleet).IsTypeOf<EFCorePostgresStartupFleetStatusSource>()
      .Because("the startup report's fleet section reads wh_service_instances through this source");

    await Assert.That(second.ServiceProvider.GetService<IPerspectiveCheckpointCompleter>())
      .IsNotSameReferenceAs(completer)
      .Because("the completer writes through the DbContext of the scope that asked for it, so it cannot be shared across scopes");
    await Assert.That(second.ServiceProvider.GetService<Whizbang.Core.Lineage.IApplyStackQuery>())
      .IsSameReferenceAs(applyStack)
      .Because("the query opens its own scope per call, so one instance serves the whole host");
    await Assert.That(second.ServiceProvider.GetService<Whizbang.Core.Startup.IStartupFleetStatusSource>())
      .IsSameReferenceAs(fleet)
      .Because("the fleet source opens its own scope per call, so one instance serves the whole host");
  }

  /// <summary>
  /// Set-based perspective writes are the default (#2246), so their write sink is registered by the driver itself: a
  /// service that registers nothing resolves the Postgres sink over its own DbContext, one per scope.
  /// </summary>
  [Test]
  public async Task Postgres_RegistersThePerspectiveWriteSinkOverTheConsumersDbContextAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddWhizbang();

    await using var dataSource = new NpgsqlDataSourceBuilder(OFFLINE_CONNECTION_STRING).Build();
    services.AddSingleton(dataSource);
    services.AddDbContext<DriverSelectorTestDbContext>(o => o.UseNpgsql(dataSource));
    _ = new WhizbangPerspectiveBuilder(services)
      .WithEFCore<DriverSelectorTestDbContext>()
      .WithDriver.Postgres;

    await using var provider = services.BuildServiceProvider();
    using var first = provider.CreateScope();
    using var second = provider.CreateScope();
    var sink = first.ServiceProvider.GetService<IPerspectiveWriteSink>();

    await Assert.That(sink).IsTypeOf<EFCorePostgresPerspectiveWriteSink>()
      .Because("the set-based write mode resolves its sink here; nothing else may be needed for the default to work");
    await Assert.That(second.ServiceProvider.GetService<IPerspectiveWriteSink>()).IsNotSameReferenceAs(sink)
      .Because("the sink writes through the DbContext of the scope that asked for it");
  }

  /// <summary>A service's own sink, registered first, wins over the driver's default.</summary>
  [Test]
  public async Task Postgres_KeepsAServicesOwnPerspectiveWriteSinkAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddWhizbang();
    var own = new RecordingWriteSink();
    services.AddSingleton<IPerspectiveWriteSink>(own);

    await using var dataSource = new NpgsqlDataSourceBuilder(OFFLINE_CONNECTION_STRING).Build();
    services.AddSingleton(dataSource);
    services.AddDbContext<DriverSelectorTestDbContext>(o => o.UseNpgsql(dataSource));
    _ = new WhizbangPerspectiveBuilder(services)
      .WithEFCore<DriverSelectorTestDbContext>()
      .WithDriver.Postgres;

    await using var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();

    await Assert.That(scope.ServiceProvider.GetService<IPerspectiveWriteSink>()).IsSameReferenceAs(own);
  }

  private sealed class RecordingWriteSink : IPerspectiveWriteSink {
    public Task<IReadOnlyList<PerspectiveWriteOutcome>> WriteAsync(
        IReadOnlyList<PendingPerspectiveWrite> writes, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<PerspectiveWriteOutcome>>([]);
  }

  /// <summary>
  /// Set-based writes are the default (#2246), so every seam they run on is registered by W! itself: a service that
  /// registers nothing but its DbContext and the driver resolves the state source, the flush policy and the write sink.
  /// </summary>
  [Test]
  public async Task Postgres_RegistersEverySetBasedWriteSeamAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddWhizbang();

    await using var dataSource = new NpgsqlDataSourceBuilder(OFFLINE_CONNECTION_STRING).Build();
    services.AddSingleton(dataSource);
    services.AddDbContext<DriverSelectorTestDbContext>(o => o.UseNpgsql(dataSource));
    _ = new WhizbangPerspectiveBuilder(services)
      .WithEFCore<DriverSelectorTestDbContext>()
      .WithDriver.Postgres;

    await using var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();

    await Assert.That(scope.ServiceProvider.GetService<IPerspectiveStateSource>()).IsTypeOf<BatchedPerspectiveStateSource>()
      .Because("a batch reads its rows with one statement per table only if the state source is there by default");
    await Assert.That(scope.ServiceProvider.GetService<IApplyBatchStrategy>()).IsTypeOf<FlushEveryBatchStrategy>()
      .Because("the worker's flush policy is registered with the worker");
    await Assert.That(scope.ServiceProvider.GetService<IPerspectiveWriteSink>()).IsTypeOf<EFCorePostgresPerspectiveWriteSink>();
    await Assert.That(scope.ServiceProvider.GetKeyedService<IPerspectiveWriteSink>(typeof(DriverSelectorTestDbContext)))
      .IsTypeOf<EFCorePostgresPerspectiveWriteSink>()
      .Because("a prepared write names its context, and the batch resolves that context's sink by its type");
  }

  /// <summary>A service's own state source and flush policy, registered first, win over W!'s defaults.</summary>
  [Test]
  public async Task Postgres_KeepsAServicesOwnStateSourceAndFlushPolicyAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    var source = new BatchedPerspectiveStateSource();
    var policy = new FlushEveryBatchStrategy();
    services.AddSingleton<IPerspectiveStateSource>(source);
    services.AddSingleton<IApplyBatchStrategy>(policy);
    services.AddWhizbang();

    await using var dataSource = new NpgsqlDataSourceBuilder(OFFLINE_CONNECTION_STRING).Build();
    services.AddSingleton(dataSource);
    services.AddDbContext<DriverSelectorTestDbContext>(o => o.UseNpgsql(dataSource));
    _ = new WhizbangPerspectiveBuilder(services)
      .WithEFCore<DriverSelectorTestDbContext>()
      .WithDriver.Postgres;

    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetService<IPerspectiveStateSource>()).IsSameReferenceAs(source);
    await Assert.That(provider.GetService<IApplyBatchStrategy>()).IsSameReferenceAs(policy);
  }

  /// <summary>
  /// A service with two DbContexts writes each perspective through its own (#2246): each context registered through the
  /// driver gets a sink of its own, keyed by its type, over that context.
  /// </summary>
  [Test]
  public async Task Postgres_WithTwoContexts_RegistersASinkOverEachAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddWhizbang();

    await using var dataSource = new NpgsqlDataSourceBuilder(OFFLINE_CONNECTION_STRING).Build();
    services.AddSingleton(dataSource);
    services.AddDbContext<DriverSelectorTestDbContext>(o => o.UseNpgsql(dataSource));
    services.AddDbContext<SecondDriverTestDbContext>(o => o.UseNpgsql(dataSource));
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<DriverSelectorTestDbContext>().WithDriver.Postgres;
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<SecondDriverTestDbContext>().WithDriver.Postgres;

    await using var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    var first = scope.ServiceProvider.GetKeyedService<IPerspectiveWriteSink>(typeof(DriverSelectorTestDbContext));
    var second = scope.ServiceProvider.GetKeyedService<IPerspectiveWriteSink>(typeof(SecondDriverTestDbContext));

    await Assert.That(first).IsTypeOf<EFCorePostgresPerspectiveWriteSink>();
    await Assert.That(second).IsTypeOf<EFCorePostgresPerspectiveWriteSink>();
    await Assert.That(((EFCorePostgresPerspectiveWriteSink)first!).Context)
      .IsSameReferenceAs(scope.ServiceProvider.GetRequiredService<DriverSelectorTestDbContext>());
    await Assert.That(((EFCorePostgresPerspectiveWriteSink)second!).Context)
      .IsSameReferenceAs(scope.ServiceProvider.GetRequiredService<SecondDriverTestDbContext>())
      .Because("the second context's rows are written through the second context, never the first");
  }

  // The digest is what lets a rebuild of named streams say whether it CHANGED anything, as opposed to merely
  // having run (#1135). It is resolved from the consumer's own DbContext and service identity, so a registration
  // that cannot be constructed would leave every rebuild reporting "not known" with nothing to explain why. The
  // sweep above resolves what the driver registers generically; this names the service, so the failure message
  // points at the digest rather than at a list.
  [Test]
  public async Task Postgres_RegistersTheRowDigestSoARebuildCanSayWhatItChangedAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddWhizbang();

    await using var dataSource = new NpgsqlDataSourceBuilder(OFFLINE_CONNECTION_STRING).Build();
    services.AddSingleton(dataSource);
    services.AddDbContext<DriverSelectorTestDbContext>(o => o.UseNpgsql(dataSource));

    _ = new WhizbangPerspectiveBuilder(services)
      .WithEFCore<DriverSelectorTestDbContext>()
      .WithDriver.Postgres;

    await using var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();

    var digest = scope.ServiceProvider.GetService<IPerspectiveRowDigest>();

    await Assert.That(digest).IsNotNull()
      .Because("without it every selected-streams rebuild records no digest and cannot report whether it changed anything");
    await Assert.That(digest!.GetType().Name)
      .IsEqualTo("EFCorePostgresPerspectiveRowDigest");
  }

}

/// <summary>A second DbContext, for a service that keeps its perspectives in two contexts.</summary>
public class SecondDriverTestDbContext(DbContextOptions<SecondDriverTestDbContext> options) : DbContext(options);
