using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Strict RED→GREEN regression tests for the 2026-06-12 Bijan Camp report:
/// <c>EFCoreServiceRegistrationGenerator</c> emitted a temp-provider pattern in
/// two distinct sites that, in a host whose <c>IConfiguration</c> is registered
/// via factory (every minimal/generic host built by
/// <c>WebApplication.CreateBuilder</c> / <c>HostApplicationBuilder</c>), caused
/// the host's shared <see cref="ConfigurationManager"/> to be disposed as a
/// side effect — silently killing every change-token subscription downstream
/// (<see cref="ChangeToken.OnChange"/>, <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/>,
/// <c>IFeatureManager</c>, <c>appsettings.json reloadOnChange</c>, Azure App
/// Configuration refresh).
///
/// <para>Unlike <c>tests/Whizbang.Hosting.AspNet.Tests/Configuration/
/// HostConfigurationDisposalTests.cs</c> which replicates the buggy and fixed
/// patterns inline, these tests exercise the <strong>actual generator
/// output</strong>: site 1 via the generated <c>AddWorkCoordinationDbContext()</c>
/// extension method, and site 2 via the <c>AddWhizbang(…).WithEFCore&lt;T&gt;().
/// WithDriver.Postgres</c> chain that invokes the
/// <c>DbContextRegistrationRegistry.Register&lt;T&gt;(…)</c> callback the
/// generator emits in its module initializer.</para>
///
/// <para>Both tests pass with the post-fix generator (this commit) and fail
/// with the pre-fix generator (verified by reverting the
/// <c>EFCoreServiceRegistrationGenerator.cs</c> patch and rebuilding).</para>
/// </summary>
[Category("Shard2")]
public class GeneratedExtensionDoesNotDisposeConfigurationManagerTests {

  [Test]
  public async Task Site1_AddWorkCoordinationDbContext_DoesNotDisposeConfigurationManagerAsync() {
    // Reproduce the host's factory registration shape for IConfiguration:
    // WebApplication.CreateBuilder / HostApplicationBuilder register the shared
    // ConfigurationManager via a factory, so any container that *resolves*
    // IConfiguration becomes its owner. The buggy generator output used to
    // resolve it from a temp container and dispose it via `using`.
    var configManager = new ConfigurationManager();
    var probe = new ProbeSource();
    configManager.Sources.Add(probe);
    // Pre-populate the connection string the generated factory expects — covers
    // the post-fix path which resolves it lazily when NpgsqlDataSource is
    // resolved (we don't resolve it here so no actual DB connection is required).
    configManager["ConnectionStrings:workcoordination-db"] = _FAKE_CONNECTION_STRING;

    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(_ => configManager);
    services.AddLogging();

    // SITE 1 — the generated turnkey extension method (emitted around line 1497
    // of EFCoreServiceRegistrationGenerator.cs).
    services.AddWorkCoordinationDbContext();

    // Verify the host's ConfigurationManager is still alive by triggering a
    // provider-level reload and asserting the change-token subscription fires.
    var fired = 0;
    using var subscription = ChangeToken.OnChange(
      () => ((IConfiguration)configManager).GetReloadToken(),
      () => Interlocked.Increment(ref fired));

    probe.Provider!.TriggerReload();

    await Assert.That(Volatile.Read(ref fired)).IsEqualTo(1)
      .Because("After AddWorkCoordinationDbContext() (Site 1 of the EFCore generator's emit), the host's ConfigurationManager MUST still propagate reloads. The pre-fix generator built `using var tempProvider = services.BuildServiceProvider(); var config = tempProvider.GetRequiredService<IConfiguration>();` which made the temp container the CM's owner; the `using` then disposed both. That disposal destroyed every provider→CM change-token subscription, so this assertion would land at 0. Post-fix the factory defers IConfiguration access to provider-build time — no temp container, no disposal.");
  }

  [Test]
  public async Task Site2_AddWhizbangWithEFCorePostgres_DoesNotDisposeConfigurationManagerAsync() {
    // SITE 2 — the DbContextRegistrationRegistry.Register<T>(…) callback the
    // generator emits in its module initializer (around line 1636 of
    // EFCoreServiceRegistrationGenerator.cs). This is the path a consumer hits
    // because every microservice uses the `AddWhizbang(…).WithEFCore<TContext>().
    // WithDriver.Postgres` chain rather than the turnkey `AddXxxDbContext()`.
    var configManager = new ConfigurationManager();
    var probe = new ProbeSource();
    configManager.Sources.Add(probe);
    configManager["ConnectionStrings:workcoordination-db"] = _FAKE_CONNECTION_STRING;

    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(_ => configManager);
    services.AddLogging();

    // SITE 2 invocation — the AddWhizbang chain that resolves the generated
    // callback via PostgresDriverExtensions.Postgres.
    _ = services.AddWhizbang(options => {
      options.DefaultQueryScope = QueryScope.Tenant;
    })
      .WithEFCore<WorkCoordinationDbContext>()
      .WithDriver.Postgres;

    var fired = 0;
    using var subscription = ChangeToken.OnChange(
      () => ((IConfiguration)configManager).GetReloadToken(),
      () => Interlocked.Increment(ref fired));

    probe.Provider!.TriggerReload();

    await Assert.That(Volatile.Read(ref fired)).IsEqualTo(1)
      .Because("After AddWhizbang().WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres (Site 2 of the EFCore generator's emit — the path every consumer microservice hits), the host's ConfigurationManager MUST still propagate reloads. Pre-fix this assertion lands at 0 because the temp provider built inside the registered callback disposed the shared ConfigurationManager when the callback returned. Post-fix the callback defers IConfiguration to provider-build time via a factory.");
  }

  // ============================================================================
  // helpers
  // ============================================================================

  // Syntactically valid Npgsql connection string. No connection is opened by
  // either code path under test — the post-fix NpgsqlDataSource factory is
  // lazy (only runs when NpgsqlDataSource is resolved), and we never resolve
  // it. Required only because the pre-fix code path read the value via
  // GetConnectionString and threw on null.
  private const string _FAKE_CONNECTION_STRING =
    "Host=localhost;Database=test;Username=test;Password=test";

  private sealed class ProbeSource : IConfigurationSource {
    public ProbeProvider? Provider;
    public IConfigurationProvider Build(IConfigurationBuilder b) => Provider = new ProbeProvider();
  }

  private sealed class ProbeProvider : ConfigurationProvider {
    public void TriggerReload() => OnReload();
  }
}
