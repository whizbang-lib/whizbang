using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Serialization;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Resolution coverage for the <c>IEventStore</c> factory lambda that appears — identically —
/// inside both full <c>AddWhizbangPostgres</c> overloads (the perspectiveEntries overload and the
/// perspectiveSchemaSql overload). Registering <c>services.AddScoped&lt;IEventStore&gt;(sp => ...)</c>
/// only creates a <c>ServiceDescriptor</c>; nothing inside the lambda body runs until something
/// actually resolves <c>IEventStore</c>.
/// <para>
/// <c>DecorateEventStoreWithSyncTracking</c> (called at the end of both overloads) removes that
/// descriptor and re-registers it behind an <c>InnerEventStoreHolder</c> factory that invokes the
/// ORIGINAL factory delegate to build the inner store before wrapping it in the decorator stack.
/// So resolving <c>IEventStore</c> from a built provider is also the only way to prove the wrapped
/// factory's dependency graph — <c>IDbConnectionFactory</c>, <c>IDbExecutor</c>,
/// <c>JsonSerializerOptions</c>, the JSONB adapter, <c>JsonbSizeValidator</c>, <c>IPolicyEngine</c>,
/// the optional <c>IPerspectiveInvoker</c>, and <c>ILogger&lt;DapperPostgresEventStore&gt;</c> — is
/// actually satisfiable, rather than merely asserting the descriptor exists.
/// </para>
/// <para>
/// Both full overloads eagerly wait for a live PostgreSQL connection
/// (<c>PostgresConnectionRetry.WaitForConnectionAsync</c>) before registering anything, so even
/// though the <c>IEventStore</c> factory body itself performs no I/O (every constructor in the
/// decorator chain only stashes its dependencies), calling either overload at all — and therefore
/// reaching the factory lambda — still requires the shared container, matching the sibling
/// <c>ServiceCollectionExtensions_FullOverloadRegistrationTests</c>.
/// </para>
/// </summary>
public class ServiceCollectionExtensionsCoverageTests : IAsyncDisposable {
  private static readonly KeyValuePair<string, string>[] _emptyEntries = [];

  private string? _testDatabaseName;
  private string? _connectionString;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();

    _testDatabaseName = $"test_{Guid.NewGuid():N}";
    await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
    await adminConnection.OpenAsync();
    await adminConnection.ExecuteAsync($"CREATE DATABASE {_testDatabaseName}");

    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _testDatabaseName,
      Timezone = "UTC",
      IncludeErrorDetail = true
    };
    _connectionString = builder.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_testDatabaseName != null) {
      try {
        await using var adminConnection = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await adminConnection.OpenAsync();
        await adminConnection.ExecuteAsync($@"
          SELECT pg_terminate_backend(pg_stat_activity.pid)
          FROM pg_stat_activity
          WHERE pg_stat_activity.datname = '{_testDatabaseName}'
          AND pid <> pg_backend_pid()");
        await adminConnection.ExecuteAsync($"DROP DATABASE IF EXISTS {_testDatabaseName} WITH (FORCE)");
      } catch { /* ignore cleanup errors */ }
      _testDatabaseName = null;
      _connectionString = null;
    }
  }

  public async ValueTask DisposeAsync() {
    await TeardownAsync();
    GC.SuppressFinalize(this);
  }

  // If the IEventStore factory inside the perspectiveEntries overload stops being able to resolve
  // one of its dependencies (IDbConnectionFactory, IDbExecutor, the JSONB adapter,
  // JsonbSizeValidator, IPolicyEngine, or ILogger<DapperPostgresEventStore>), an application that
  // registers this overload at startup never sees the problem there — registration only builds a
  // ServiceDescriptor. The break only surfaces the first time some handler asks the container for
  // IEventStore, as an unhandled DI resolution exception in production.
  [Test]
  public async Task AddWhizbangPostgres_EntriesOverload_EventStoreFactory_ResolvesScopedDecoratedStoreAsync() {
    var services = new ServiceCollection();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddLogging();
    services.AddSingleton<IPerspectiveSyncAwaiter>(new NoopPerspectiveSyncAwaiter());

    services.AddWhizbangPostgres(
      _connectionString!, jsonOptions, initializeSchema: false, _emptyEntries, configureOptions: null);

    await using var provider = services.BuildServiceProvider();

    using var scopeOne = provider.CreateScope();
    var firstResolution = scopeOne.ServiceProvider.GetRequiredService<IEventStore>();
    var secondResolutionSameScope = scopeOne.ServiceProvider.GetRequiredService<IEventStore>();

    await Assert.That(firstResolution).IsTypeOf<AppendAndWaitEventStoreDecorator>()
      .Because("DecorateEventStoreWithSyncTracking must wrap the DapperPostgresEventStore built by the factory lambda so callers get AppendAndWaitAsync support.");
    await Assert.That(secondResolutionSameScope).IsSameReferenceAs(firstResolution)
      .Because("IEventStore is registered Scoped — the same scope must reuse the same instance rather than re-running the factory on every resolution.");

    using var scopeTwo = provider.CreateScope();
    var resolutionInOtherScope = scopeTwo.ServiceProvider.GetRequiredService<IEventStore>();

    await Assert.That(resolutionInOtherScope).IsNotSameReferenceAs(firstResolution)
      .Because("A new scope must re-run the factory lambda and build a fresh DapperPostgresEventStore, proving the registration is genuinely Scoped rather than accidentally cached as a singleton.");
  }

  // Same factory body, duplicated verbatim in the perspectiveSchemaSql overload. The two overloads
  // are independent method bodies, so a regression here (a missing dependency, a swapped
  // constructor argument, a wrong lifetime) breaks applications using THIS overload the same way —
  // at first IEventStore use, not at startup — even while the entries-overload test above still
  // passes untouched.
  [Test]
  public async Task AddWhizbangPostgres_SchemaSqlOverload_EventStoreFactory_ResolvesScopedDecoratedStoreAsync() {
    var services = new ServiceCollection();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddLogging();
    services.AddSingleton<IPerspectiveSyncAwaiter>(new NoopPerspectiveSyncAwaiter());

    services.AddWhizbangPostgres(
      _connectionString!, jsonOptions, initializeSchema: false, perspectiveSchemaSql: null, configureOptions: null);

    await using var provider = services.BuildServiceProvider();

    using var scopeOne = provider.CreateScope();
    var firstResolution = scopeOne.ServiceProvider.GetRequiredService<IEventStore>();
    var secondResolutionSameScope = scopeOne.ServiceProvider.GetRequiredService<IEventStore>();

    await Assert.That(firstResolution).IsTypeOf<AppendAndWaitEventStoreDecorator>()
      .Because("DecorateEventStoreWithSyncTracking must wrap the DapperPostgresEventStore built by this overload's factory lambda so callers get AppendAndWaitAsync support.");
    await Assert.That(secondResolutionSameScope).IsSameReferenceAs(firstResolution)
      .Because("IEventStore is registered Scoped — the same scope must reuse the same instance rather than re-running the factory on every resolution.");

    using var scopeTwo = provider.CreateScope();
    var resolutionInOtherScope = scopeTwo.ServiceProvider.GetRequiredService<IEventStore>();

    await Assert.That(resolutionInOtherScope).IsNotSameReferenceAs(firstResolution)
      .Because("A new scope must re-run the factory lambda and build a fresh DapperPostgresEventStore, proving the registration is genuinely Scoped rather than accidentally cached as a singleton.");
  }

  // Minimal IPerspectiveSyncAwaiter stand-in: AppendAndWaitEventStoreDecorator's constructor only
  // stashes this dependency (via GetRequiredService), it is never invoked while merely resolving
  // IEventStore, so every method below is unreachable in these tests.
  private sealed class NoopPerspectiveSyncAwaiter : IPerspectiveSyncAwaiter {
    public Guid AwaiterId { get; } = Guid.NewGuid();

    public Task<SyncResult> WaitAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) =>
      throw new NotSupportedException("Not exercised by these registration/resolution coverage tests.");

    public Task<bool> IsCaughtUpAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) =>
      throw new NotSupportedException("Not exercised by these registration/resolution coverage tests.");

    public Task<SyncResult> WaitForStreamAsync(
        Type perspectiveType,
        Guid streamId,
        Type[]? eventTypes,
        TimeSpan timeout,
        Guid? eventIdToAwait = null,
        CancellationToken ct = default) =>
      throw new NotSupportedException("Not exercised by these registration/resolution coverage tests.");
  }
}
