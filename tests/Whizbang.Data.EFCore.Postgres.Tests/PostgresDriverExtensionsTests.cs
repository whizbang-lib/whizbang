// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#nullable disable
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Health;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Perspectives;
using Whizbang.Core.RunControl;
using Whizbang.Data.EFCore.Postgres.Dispatch;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Test model for PostgresDriverExtensions tests.
/// </summary>
[Category("Shard1")]
public class PostgresTestModel {
  public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Test DbContext with ConfigurePerspectiveRow for auto-discovery.
/// </summary>
public class PostgresTestDbContext(DbContextOptions<PostgresTestDbContext> options) : DbContext(options) {
  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    // This call will be discovered by EFCoreServiceRegistrationGenerator
    modelBuilder.Entity<PerspectiveRow<PostgresTestModel>>(entity => entity.HasKey(e => e.Id));
  }
}

/// <summary>
/// A context with no entities, so its model builds in memory: the table statistics read its default schema.
/// </summary>
public class StatisticsWiringDbContext(DbContextOptions<StatisticsWiringDbContext> options) : DbContext(options);

/// <summary>
/// Tests for PostgresDriverExtensions (.Postgres property).
/// Verifies driver registration, service configuration, and error handling.
/// Target: 100% branch coverage.
/// </summary>
[Category("Shard4")]
public class PostgresDriverExtensionsTests {
  [Test]
  public async Task Postgres_WithValidEFCoreSelector_ReturnsWhizbangPerspectiveBuilderAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(options =>
        options.UseInMemoryDatabase("TestDb"));

    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<PostgresTestDbContext>();

    // Act
    var result = selector.WithDriver.Postgres;

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsTypeOf<WhizbangPerspectiveBuilder>();
  }

  [Test]
  public async Task Postgres_ReturnedBuilder_HasSameServicesAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(options =>
        options.UseInMemoryDatabase("TestDb"));

    var builder = new WhizbangPerspectiveBuilder(services);
    var selector = builder.WithEFCore<PostgresTestDbContext>();

    // Act
    var result = selector.WithDriver.Postgres;

    // Assert
    await Assert.That(result.Services).IsSameReferenceAs(services);
  }

  [Test]
  public async Task Postgres_WithNonEFCoreDriverOptions_ThrowsInvalidOperationExceptionAsync() {
    // Arrange - create a fake IDriverOptions that isn't EFCoreDriverSelector
    var services = new ServiceCollection();
    IDriverOptions fakeOptions = new FakeDriverOptions(services);

    // Act & Assert
    var exception = await Assert.That(() => fakeOptions.Postgres)
        .Throws<InvalidOperationException>();

    await Assert.That(exception.Message).Contains("Postgres driver can only be used with EF Core storage");
    await Assert.That(exception.Message).Contains("Call .WithEFCore<TDbContext>() before .WithDriver.Postgres");
  }

  // Removed Postgres_*ReadinessCheck* tests — Phase H decoupled startup gating from
  // IDatabaseReadinessCheck. Workers now wait on ISchemaReadyGate. The Postgres driver
  // extensions no longer register IDatabaseReadinessCheck.

  [Test]
  public async Task Postgres_RegistersDbContextNotificationConnectionStringFallbackAsync() {
    // Locks the DI wiring that lets PgWorkNotificationListener +
    // PgCommitOrderStamperWorker reach the DbContext-backed fallback when
    // Whizbang:Database is unconfigured. The connection-string round-trip itself is
    // covered by DbContextNotificationConnectionStringFallbackTests — here we just
    // verify the singleton lands in DI with the right concrete type.
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(o => o.UseInMemoryDatabase("TestDb"));

    var builder = new WhizbangPerspectiveBuilder(services);
    _ = builder.WithEFCore<PostgresTestDbContext>().WithDriver.Postgres;

    await using var sp = services.BuildServiceProvider();
    var fallback = sp.GetService<INotificationConnectionStringFallback>();

    await Assert.That(fallback).IsNotNull();
    await Assert.That(fallback).IsTypeOf<DbContextNotificationConnectionStringFallback>();
  }

  // ── Postgres extensions as capabilities (#1498, #1513) ──────────────────────────────────────────

  [Test]
  public async Task Postgres_RegistersItsDatabaseAsACapabilityScope_UnderItsConnectionStringNameAsync() {
    await Whizbang.Testing.Containers.SharedPostgresContainer.InitializeAsync();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDbContext<PostgresTestDbContext>(o => o.UseInMemoryDatabase("TestDb"));
    await using var dataSource = NpgsqlDataSource.Create(Whizbang.Testing.Containers.SharedPostgresContainer.ConnectionString);
    services.AddSingleton(dataSource);
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<PostgresTestDbContext>("orders-db").WithDriver.Postgres;

    await using var sp = services.BuildServiceProvider();
    var registry = sp.GetRequiredService<Whizbang.Core.Capabilities.ICapabilityRegistry>();
    await registry.RefreshAsync(CancellationToken.None);

    await Assert.That(registry.Providers.Single().Scopes.Single()).IsEqualTo("orders-db")
      .Because("the same name keys the database's Whizbang:Postgres:<name> section");
    await Assert.That(registry.Statuses.All(s => s.Reason is null)).IsTrue()
      .Because("the probe opens its connection from the context's data source");
    await Assert.That(registry.GetState(new Whizbang.Core.Capabilities.CapabilityId(PostgresExtensionCatalog.PROVIDER, "pg_squeeze"), "orders-db"))
      .IsEqualTo(Whizbang.Core.Capabilities.CapabilityState.Missing);
    await Assert.That(sp.GetServices<Whizbang.Core.Startup.IStartupStep>().OfType<Whizbang.Core.Capabilities.CapabilitiesStartupStep>().Count()).IsEqualTo(1);
  }

  /// <summary>Answers one state for everything and records what it was asked.</summary>
  private sealed class FixedStateRegistry(Whizbang.Core.Capabilities.CapabilityState state)
      : Whizbang.Core.Capabilities.ICapabilityRegistry {
    public List<(Whizbang.Core.Capabilities.CapabilityId Id, string Scope)> Asked { get; } = [];
    public IReadOnlyList<Whizbang.Core.Capabilities.ICapabilityProvider> Providers => [];
    public IReadOnlyList<Whizbang.Core.Capabilities.CapabilityStatus> Statuses => [];
    public Whizbang.Core.Capabilities.CapabilityVerdict Verdict => Whizbang.Core.Capabilities.CapabilityVerdict.Empty;

    public Whizbang.Core.Capabilities.CapabilityState GetState(Whizbang.Core.Capabilities.CapabilityId id, string scope) {
      Asked.Add((id, scope));
      return state;
    }

    public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Record(IReadOnlyList<Whizbang.Core.Capabilities.CapabilityStatus> statuses) {
      // Nothing to record: the state is fixed.
    }
    public void PublishVerdict(Whizbang.Core.Capabilities.CapabilityVerdict verdict) {
      // Nothing to publish: the verdict is not under test.
    }
    public Task<Whizbang.Core.Capabilities.CapabilityVerdict> WaitForVerdictAsync(CancellationToken cancellationToken) =>
      Task.FromResult(Whizbang.Core.Capabilities.CapabilityVerdict.Empty);
  }

  /// <summary>
  /// Issue #1754: the driver hands its table statistics the registry and the name its database is probed under, so
  /// statement statistics are read only while the probe found pg_stat_statements Ready on that database.
  /// </summary>
  [Test]
  public async Task Postgres_TableStatistics_ConsultTheStatementStatisticsCapability_OfItsOwnDatabaseAsync() {
    var registry = new FixedStateRegistry(Whizbang.Core.Capabilities.CapabilityState.NeedsServerChange);
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<Whizbang.Core.Capabilities.ICapabilityRegistry>(registry);
    // A context with no entities: resolving the statistics builds its model to read the default schema.
    services.AddDbContext<StatisticsWiringDbContext>(o => o.UseInMemoryDatabase("StatisticsWiring"));
    await using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused");
    services.AddSingleton(dataSource);
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<StatisticsWiringDbContext>("orders-db").WithDriver.Postgres;
    await using var sp = services.BuildServiceProvider();

    var predicates = await sp.GetRequiredService<Whizbang.Core.Observability.ITableStatisticsProvider>()
      .GetExpensivePredicatesAsync();

    await Assert.That(predicates).IsEmpty();
    await Assert.That(registry.Asked).IsEquivalentTo([(
      new Whizbang.Core.Capabilities.CapabilityId(PostgresExtensionCatalog.PROVIDER, "pg_stat_statements"), "orders-db")])
      .Because("the scope is the name the driver registered its database under for the probe");
  }

  // ── Turnkey IClaimedEmissionStore registration ────────────────────────────────

  [Test]
  public async Task Postgres_RegistersIClaimedEmissionStore_ScopedAsync() {
    // The Postgres driver routes saga completion through PublishOnceAsync, which requires
    // IClaimedEmissionStore. Before this turnkey wiring, every Postgres consumer had to
    // register EFCoreClaimedEmissionStore manually — a consumer had to add the registration in
    // both Program.cs and the integration fixture to avoid PublishOnceAsync throwing at
    // saga completion (a production saga regression). With this in place, .WithDriver.Postgres
    // is genuinely turnkey for the dispatcher's exactly-once primitive.
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(o => o.UseInMemoryDatabase("TestDb"));

    var builder = new WhizbangPerspectiveBuilder(services);
    _ = builder.WithEFCore<PostgresTestDbContext>().WithDriver.Postgres;

    await using var sp = services.BuildServiceProvider();
    using var scope = sp.CreateScope();
    var store = scope.ServiceProvider.GetService<IClaimedEmissionStore>();

    await Assert.That(store).IsNotNull()
      .Because(".WithDriver.Postgres MUST turnkey-register IClaimedEmissionStore so consumers " +
               "don't have to wire it manually; PublishOnceAsync throws without it.");
    await Assert.That(store).IsTypeOf<EFCoreClaimedEmissionStore>();

    var descriptor = services.First(sd => sd.ServiceType == typeof(IClaimedEmissionStore));
    await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Scoped)
      .Because("EFCoreClaimedEmissionStore holds a DbContext; lifetime must match the DbContext " +
               "scope so the same transaction sees the claim row.");
  }

  /// <summary>
  /// The driver that registers the claim store also registers the step that prunes its expired claims
  /// (#999), once however often the driver is selected.
  /// </summary>
  [Test]
  public async Task Postgres_RegistersTheClaimPruneStep_OnceAsync() {
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(o => o.UseInMemoryDatabase("TestDb"));

    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<PostgresTestDbContext>().WithDriver.Postgres;
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<PostgresTestDbContext>().WithDriver.Postgres;

    await using var sp = services.BuildServiceProvider();
    using var scope = sp.CreateScope();
    var steps = scope.ServiceProvider.GetServices<Whizbang.Core.Workers.IMaintenanceStep>()
      .OfType<Whizbang.Core.Workers.ClaimedEmissionPruneStep>().ToList();
    await Assert.That(steps).Count().IsEqualTo(1);
  }

  /// <summary>
  /// A host that has logging gets the prune step's report in its own log: the registration hands the
  /// step the host's logger and falls back to a null one only where the host has none.
  /// </summary>
  [Test]
  public async Task Postgres_TheClaimPruneStepLogsThroughTheHostsLoggerAsync() {
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(o => o.UseInMemoryDatabase("TestDb"));
    var hostLogger = new CapturingPruneLogger();
    services.AddSingleton<ILogger<Whizbang.Core.Workers.ClaimedEmissionPruneStep>>(hostLogger);
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<PostgresTestDbContext>().WithDriver.Postgres;

    await using var sp = services.BuildServiceProvider();
    using var scope = sp.CreateScope();
    var step = scope.ServiceProvider.GetServices<Whizbang.Core.Workers.IMaintenanceStep>()
      .OfType<Whizbang.Core.Workers.ClaimedEmissionPruneStep>().Single();

    // The step prunes through whatever claim store the run's services hold; this one reports three.
    var runServices = new ServiceCollection()
      .AddSingleton<IClaimedEmissionStore>(new PrunesThreeClaimedEmissionStore())
      .BuildServiceProvider();
    await step.RunAsync(runServices, CancellationToken.None);

    await Assert.That(hostLogger.Messages).Count().IsEqualTo(1);
    await Assert.That(hostLogger.Messages[0]).Contains("Pruned 3 claimed-emission claim(s)");
  }

  /// <summary>
  /// A host without logging still gets a working prune step: the registration falls back to a logger that
  /// discards, where the step itself refuses a missing one.
  /// </summary>
  [Test]
  public async Task Postgres_TheClaimPruneStepWorksInAHostWithoutLoggingAsync() {
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(o => o.UseInMemoryDatabase("TestDb"));
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<PostgresTestDbContext>().WithDriver.Postgres;
    services.RemoveAll(typeof(ILogger<>));
    services.RemoveAll<ILoggerFactory>();

    await using var sp = services.BuildServiceProvider();
    using var scope = sp.CreateScope();
    await Assert.That(scope.ServiceProvider.GetService<ILogger<Whizbang.Core.Workers.ClaimedEmissionPruneStep>>()).IsNull();
    var step = scope.ServiceProvider.GetServices<Whizbang.Core.Workers.IMaintenanceStep>()
      .OfType<Whizbang.Core.Workers.ClaimedEmissionPruneStep>().Single();

    var store = new PrunesThreeClaimedEmissionStore();
    var runServices = new ServiceCollection().AddSingleton<IClaimedEmissionStore>(store).BuildServiceProvider();
    await step.RunAsync(runServices, CancellationToken.None);

    await Assert.That(store.PruneCalls).IsEqualTo(1)
      .Because("the step is built and prunes although the host registered no logging");
  }

  private sealed class PrunesThreeClaimedEmissionStore : IClaimedEmissionStore {
    public int PruneCalls { get; private set; }

    public Task<bool> TryClaimAsync(string claimKey, Guid claimedByEventId, CancellationToken cancellationToken)
      => Task.FromResult(true);

    public Task<int> PruneExpiredAsync(
        DateTimeOffset expiredBefore, IReadOnlyCollection<string> retainedKeyPrefixes, int maxClaims, CancellationToken cancellationToken) {
      PruneCalls++;
      return Task.FromResult(3);
    }
  }

  private sealed class CapturingPruneLogger : ILogger<Whizbang.Core.Workers.ClaimedEmissionPruneStep> {
    public List<string> Messages { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
      => Messages.Add(formatter(state, exception));
  }

  [Test]
  public async Task Postgres_DoesNotOverrideExistingClaimedEmissionStore_Async() {
    // Locks the TryAdd semantics: consumers who already supply an IClaimedEmissionStore
    // (e.g. a Dapper-backed override) keep their implementation when .WithDriver.Postgres
    // runs. Otherwise upgrading Whizbang versions would silently replace consumer overrides.
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(o => o.UseInMemoryDatabase("TestDb"));
    services.AddScoped<IClaimedEmissionStore, NoOpClaimedEmissionStore>();

    var builder = new WhizbangPerspectiveBuilder(services);
    _ = builder.WithEFCore<PostgresTestDbContext>().WithDriver.Postgres;

    await using var sp = services.BuildServiceProvider();
    using var scope = sp.CreateScope();
    var store = scope.ServiceProvider.GetService<IClaimedEmissionStore>();

    await Assert.That(store).IsTypeOf<NoOpClaimedEmissionStore>()
      .Because("TryAdd semantics: consumer-supplied override MUST win over the driver's default.");
  }

  /// <summary>
  /// Fake implementation of IDriverOptions for testing error handling.
  /// </summary>
  private sealed class FakeDriverOptions(IServiceCollection services) : IDriverOptions {
    public IServiceCollection Services { get; } = services;
  }

  private sealed class NoOpClaimedEmissionStore : IClaimedEmissionStore {
    public Task<bool> TryClaimAsync(string claimKey, Guid claimedByEventId, CancellationToken cancellationToken)
      => Task.FromResult(true);
  }

  /// <summary>
  /// The driver registers an event-store health source, and that source's probe is the driver's
  /// own — not a copy.
  /// </summary>
  /// <remarks>
  /// The registration is a factory lambda, so nothing runs it until something resolves and
  /// probes the source. That makes it exactly the shape that can be wired to the wrong thing and
  /// stay silent: the health endpoint would answer, just about something else. Resolving it here
  /// and driving it against a real data source is what ties the registered source to the probe
  /// the health-source tests cover directly.
  /// </remarks>
  [Test]
  public async Task Postgres_RegistersAnEventStoreHealthSourceWiredToTheDriversProbeAsync() {
    var services = new ServiceCollection();
    services.AddDbContext<PostgresTestDbContext>(options => options.UseInMemoryDatabase("HealthDb"));

    var builder = new WhizbangPerspectiveBuilder(services);
    _ = builder.WithEFCore<PostgresTestDbContext>().WithDriver.Postgres;

    // The probe needs a data source and the lifecycle phase; an unreachable host is fine here —
    // the point is that the registered source runs the driver's probe and reports its outcome
    // rather than answering from somewhere else.
    services.AddSingleton(NpgsqlDataSource.Create(
      "Host=127.0.0.1;Port=1;Username=nobody;Password=nobody;Database=nothing;Timeout=1"));
    services.AddSingleton<IWhizbangLifecycleState>(new RunningLifecycle());

    await using var provider = services.BuildServiceProvider();
    var source = provider.GetServices<IWhizbangHealthSource>()
      .FirstOrDefault(s => s.Component == "event-store");

    await Assert.That(source).IsNotNull()
      .Because("without it a consumer has to write its own readiness check, which is what this "
             + "registration exists to replace");

    var health = await source!.ReportAsync(CancellationToken.None);

    await Assert.That(health.State).IsEqualTo(ComponentState.Faulted)
      .Because("the registered source must report the probe's actual outcome — a source wired to "
             + "something else would answer Operational against an unreachable database");
  }

  private sealed class RunningLifecycle : IWhizbangLifecycleState {
    public LifecyclePhase Phase => LifecyclePhase.Running;
    public ValueTask AdvanceToAsync(LifecyclePhase phase, CancellationToken cancellationToken) => default;
    public ValueTask FaultAsync(CancellationToken cancellationToken) => default;
  }
}
