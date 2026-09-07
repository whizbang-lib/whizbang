using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Policies;
using Whizbang.Core.Sequencing;
using Whizbang.Core.Serialization;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Argument-validation coverage for the two full <c>AddWhizbangPostgres</c> overloads
/// (perspectiveEntries + configureOptions, and perspectiveSchemaSql + configureOptions).
/// Guard clauses throw BEFORE any connection attempt, so these are fast unit tests —
/// no Docker / PostgreSQL required. Also locks the named health-check registration.
/// </summary>
public class ServiceCollectionExtensions_FullOverloadValidationTests {
  // Never connected to — guard clauses throw before PostgresConnectionRetry runs.
  private const string DUMMY_CONNECTION_STRING = "Host=localhost;Database=whizbang_validation;Username=u;Password=p";

  private static readonly KeyValuePair<string, string>[] _emptyEntries = [];

  [Test]
  public async Task AddWhizbangPostgres_EntriesOverload_NullConnectionString_ThrowsArgumentNullExceptionAsync() {
    var services = new ServiceCollection();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

    var ex = Assert.Throws<ArgumentNullException>(() => _ = services.AddWhizbangPostgres(
      null!, jsonOptions, initializeSchema: false, _emptyEntries, configureOptions: null));

    await Assert.That(ex!.ParamName).IsEqualTo("connectionString");
  }

  [Test]
  public async Task AddWhizbangPostgres_EntriesOverload_WhitespaceConnectionString_ThrowsArgumentExceptionAsync() {
    var services = new ServiceCollection();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

    var ex = Assert.Throws<ArgumentException>(() => _ = services.AddWhizbangPostgres(
      "   ", jsonOptions, initializeSchema: false, _emptyEntries, configureOptions: null));

    await Assert.That(ex!.ParamName).IsEqualTo("connectionString");
  }

  [Test]
  public async Task AddWhizbangPostgres_EntriesOverload_NullJsonOptions_ThrowsArgumentNullExceptionAsync() {
    var services = new ServiceCollection();

    var ex = Assert.Throws<ArgumentNullException>(() => _ = services.AddWhizbangPostgres(
      DUMMY_CONNECTION_STRING, null!, initializeSchema: false, _emptyEntries, configureOptions: null));

    await Assert.That(ex!.ParamName).IsEqualTo("jsonOptions");
  }

  [Test]
  public async Task AddWhizbangPostgres_EntriesOverload_NullPerspectiveEntries_ThrowsArgumentNullExceptionAsync() {
    var services = new ServiceCollection();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

    var ex = Assert.Throws<ArgumentNullException>(() => _ = services.AddWhizbangPostgres(
      DUMMY_CONNECTION_STRING, jsonOptions, initializeSchema: false,
      (KeyValuePair<string, string>[])null!, configureOptions: null));

    await Assert.That(ex!.ParamName).IsEqualTo("perspectiveEntries");
  }

  [Test]
  public async Task AddWhizbangPostgres_EntriesConvenienceOverload_NullJsonOptions_DelegatesToFullOverloadAndThrowsAsync() {
    // The 5-arg convenience overload has no guards of its own — it must delegate to the
    // full overload, whose jsonOptions guard fires. Proves the delegation call executes.
    var services = new ServiceCollection();

    var ex = Assert.Throws<ArgumentNullException>(() => _ = services.AddWhizbangPostgres(
      DUMMY_CONNECTION_STRING, null!, initializeSchema: false, _emptyEntries));

    await Assert.That(ex!.ParamName).IsEqualTo("jsonOptions");
  }

  [Test]
  public async Task AddWhizbangPostgres_SchemaSqlOverload_NullConnectionString_ThrowsArgumentNullExceptionAsync() {
    var services = new ServiceCollection();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

    var ex = Assert.Throws<ArgumentNullException>(() => _ = services.AddWhizbangPostgres(
      null!, jsonOptions, initializeSchema: false, perspectiveSchemaSql: null, configureOptions: null));

    await Assert.That(ex!.ParamName).IsEqualTo("connectionString");
  }

  [Test]
  public async Task AddWhizbangPostgres_SchemaSqlOverload_EmptyConnectionString_ThrowsArgumentExceptionAsync() {
    var services = new ServiceCollection();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

    var ex = Assert.Throws<ArgumentException>(() => _ = services.AddWhizbangPostgres(
      string.Empty, jsonOptions, initializeSchema: false, perspectiveSchemaSql: null, configureOptions: null));

    await Assert.That(ex!.ParamName).IsEqualTo("connectionString");
  }

  [Test]
  public async Task AddWhizbangPostgres_SchemaSqlOverload_NullJsonOptions_ThrowsArgumentNullExceptionAsync() {
    var services = new ServiceCollection();

    var ex = Assert.Throws<ArgumentNullException>(() => _ = services.AddWhizbangPostgres(
      DUMMY_CONNECTION_STRING, null!, initializeSchema: false, perspectiveSchemaSql: null, configureOptions: null));

    await Assert.That(ex!.ParamName).IsEqualTo("jsonOptions");
  }

  [Test]
  public async Task AddWhizbangPostgresHealthChecks_RegistersNamedWhizbangPostgresCheckAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    // The health check's own dependency; its constructor only stashes the factory.
    services.AddSingleton<IDbConnectionFactory>(new PostgresConnectionFactory(DUMMY_CONNECTION_STRING));

    services.AddWhizbangPostgresHealthChecks();

    await using var provider = services.BuildServiceProvider();

    var healthCheckService = provider.GetService<HealthCheckService>();
    await Assert.That(healthCheckService).IsNotNull();

    var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
    var registration = options.Registrations.SingleOrDefault(r => r.Name == "whizbang_postgres");
    await Assert.That(registration).IsNotNull()
      .Because("AddWhizbangPostgresHealthChecks must register the check under the documented name 'whizbang_postgres'.");

    var check = registration!.Factory(provider);
    await Assert.That(check).IsTypeOf<PostgresHealthCheck>();
  }
}

/// <summary>
/// Integration coverage for the two full <c>AddWhizbangPostgres</c> overloads and the
/// perspectiveEntries convenience overload. Both full overloads eagerly wait for a live
/// PostgreSQL connection at registration time (PostgresConnectionRetry.WaitForConnectionAsync),
/// so these tests use the shared container with per-test database isolation.
/// <para>
/// Registration-time lines are covered by calling the overloads; the deferred factory
/// lambdas (IDbConnectionFactory, IWorkCoordinator, snapshot store, stream locker,
/// checkpoint completer, dead-letter store) only execute on RESOLUTION, so these tests
/// build a provider and resolve each service, asserting the concrete implementation type.
/// All of those constructors only stash the connection string — no I/O at resolution.
/// </para>
/// </summary>
[Category("Integration")]
public class ServiceCollectionExtensions_FullOverloadRegistrationTests : IAsyncDisposable {
  private static readonly KeyValuePair<string, string>[] _emptyEntries = [];

  private static readonly KeyValuePair<string, string>[] _perspectiveEntries = [
    new("RegistrationCoveragePerspective",
      "CREATE TABLE IF NOT EXISTS wh_per_registration_coverage (id UUID PRIMARY KEY, model_data JSONB NOT NULL);")
  ];

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

  [Test]
  public async Task AddWhizbangPostgres_EntriesConvenienceOverload_RegistersCoreServicesWithDefaultOptionsAsync() {
    // 5-arg convenience overload — must delegate to the full entries overload with
    // configureOptions: null, leaving PostgresOptions at its defaults.
    var services = new ServiceCollection();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

    var returned = services.AddWhizbangPostgres(
      _connectionString!, jsonOptions, initializeSchema: false, _emptyEntries);

    await Assert.That(returned).IsSameReferenceAs(services);

    await using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<PostgresOptions>();
    await Assert.That(options.CommandTimeoutSeconds).IsEqualTo(120)
      .Because("configureOptions is null on the convenience path, so defaults apply.");

    await Assert.That(provider.GetRequiredService<IDbConnectionFactory>()).IsTypeOf<PostgresConnectionFactory>();
    await Assert.That(provider.GetRequiredService<IWorkCoordinator>()).IsTypeOf<DapperWorkCoordinator>();
  }

  [Test]
  public async Task AddWhizbangPostgres_EntriesOverload_RegistersExpectedLifetimesAndDecoratedEventStoreAsync() {
    var services = new ServiceCollection();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

    services.AddWhizbangPostgres(
      _connectionString!, jsonOptions, initializeSchema: false, _emptyEntries, configureOptions: null);

    await Assert.That(_single(services, typeof(IDbConnectionFactory)).Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await Assert.That(_single(services, typeof(IDbExecutor)).Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await Assert.That(_single(services, typeof(IDbExecutor)).ImplementationType).IsEqualTo(typeof(DapperDbExecutor));
    await Assert.That(_single(services, typeof(IPolicyEngine)).Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await Assert.That(_single(services, typeof(IWorkCoordinator)).Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await Assert.That(_single(services, typeof(IRequestResponseStore)).Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await Assert.That(_single(services, typeof(IRequestResponseStore)).ImplementationType).IsEqualTo(typeof(DapperPostgresRequestResponseStore));
    await Assert.That(_single(services, typeof(ISequenceProvider)).Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await Assert.That(_single(services, typeof(ISequenceProvider)).ImplementationType).IsEqualTo(typeof(DapperPostgresSequenceProvider));
    await Assert.That(_single(services, typeof(IPerspectiveSnapshotStore)).Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await Assert.That(_single(services, typeof(IPerspectiveStreamLocker)).Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await Assert.That(_single(services, typeof(IMessageTypeRegistryPopulator)).Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await Assert.That(_single(services, typeof(IMessageTypeRegistryPopulator)).ImplementationType).IsEqualTo(typeof(DapperMessageTypeRegistryPopulator));
    await Assert.That(_single(services, typeof(IEventTypeRenameTool)).Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await Assert.That(_single(services, typeof(IEventTypeRenameTool)).ImplementationType).IsEqualTo(typeof(DapperEventTypeRenameTool));
    await Assert.That(_single(services, typeof(IPerspectiveCheckpointCompleter)).Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    await Assert.That(_single(services, typeof(IDeadLetterStore)).Lifetime).IsEqualTo(ServiceLifetime.Singleton);

    // IEventStore starts Scoped as DapperPostgresEventStore, then
    // DecorateEventStoreWithSyncTracking replaces it with a scoped factory
    // (the decorator stack). Exactly one IEventStore registration must remain.
    var eventStoreDescriptors = services.Where(d => d.ServiceType == typeof(IEventStore)).ToList();
    await Assert.That(eventStoreDescriptors).Count().IsEqualTo(1);
    await Assert.That(eventStoreDescriptors[0].Lifetime).IsEqualTo(ServiceLifetime.Scoped);
    await Assert.That(eventStoreDescriptors[0].ImplementationFactory).IsNotNull()
      .Because("DecorateEventStoreWithSyncTracking must have replaced the direct type registration with the decorator factory.");
    await Assert.That(eventStoreDescriptors[0].ImplementationType).IsNull();

    // initializeSchema: false must NOT register the reconciliation hosted service.
    var reconciliationHostedService = services.FirstOrDefault(d =>
      d.ServiceType == typeof(IHostedService) &&
      d.ImplementationType == typeof(MessageTypeRegistryReconciliationHostedService));
    await Assert.That(reconciliationHostedService).IsNull();
  }

  [Test]
  public async Task AddWhizbangPostgres_EntriesOverload_ResolvesRealImplementationTypesAsync() {
    // Resolution executes the deferred factory lambdas (connection factory, work
    // coordinator, snapshot store, stream locker, checkpoint completer, dead-letter
    // store). None of these constructors open a connection — they stash the string.
    var services = new ServiceCollection();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddLogging(); // JsonbSizeValidator requires a non-null ILogger<T>.
    services.AddSingleton<IMessageTypeCatalog>(new EmptyMessageTypeCatalog()); // populator + rename tool dependency

    services.AddWhizbangPostgres(
      _connectionString!, jsonOptions, initializeSchema: false, _emptyEntries, configureOptions: null);

    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<IDbConnectionFactory>()).IsTypeOf<PostgresConnectionFactory>();
    await Assert.That(provider.GetRequiredService<IDbExecutor>()).IsTypeOf<DapperDbExecutor>();
    await Assert.That(provider.GetRequiredService<IPolicyEngine>()).IsTypeOf<PolicyEngine>();
    await Assert.That(provider.GetRequiredService<IJsonbPersistenceAdapter<IMessageEnvelope>>()).IsTypeOf<EventEnvelopeJsonbAdapter>();
    await Assert.That(provider.GetRequiredService<JsonbSizeValidator>()).IsNotNull();
    await Assert.That(provider.GetRequiredService<IWorkCoordinator>()).IsTypeOf<DapperWorkCoordinator>();
    await Assert.That(provider.GetRequiredService<IRequestResponseStore>()).IsTypeOf<DapperPostgresRequestResponseStore>();
    await Assert.That(provider.GetRequiredService<ISequenceProvider>()).IsTypeOf<DapperPostgresSequenceProvider>();
    await Assert.That(provider.GetRequiredService<IPerspectiveSnapshotStore>()).IsTypeOf<DapperPerspectiveSnapshotStore>();
    await Assert.That(provider.GetRequiredService<IPerspectiveStreamLocker>()).IsTypeOf<DapperPerspectiveStreamLocker>()
      .Because("IOptions<PerspectiveStreamLockOptions> must be satisfiable from the AddOptions infrastructure wired inside AddWhizbangPostgres.");
    await Assert.That(provider.GetRequiredService<IMessageTypeRegistryPopulator>()).IsTypeOf<DapperMessageTypeRegistryPopulator>();
    await Assert.That(provider.GetRequiredService<IEventTypeRenameTool>()).IsTypeOf<DapperEventTypeRenameTool>();
    await Assert.That(provider.GetRequiredService<IPerspectiveCheckpointCompleter>()).IsTypeOf<DapperPostgresPerspectiveCheckpointCompleter>();
    await Assert.That(provider.GetRequiredService<IDeadLetterStore>()).IsTypeOf<DapperDeadLetterStore>();

    // The exact JsonSerializerOptions and PostgresOptions instances must be registered.
    await Assert.That(provider.GetRequiredService<JsonSerializerOptions>()).IsSameReferenceAs(jsonOptions);
    await Assert.That(provider.GetRequiredService<PostgresOptions>()).IsNotNull();

    // Singletons must be cached — a second resolution returns the same instance.
    await Assert.That(provider.GetRequiredService<IWorkCoordinator>())
      .IsSameReferenceAs(provider.GetRequiredService<IWorkCoordinator>());
  }

  [Test]
  public async Task AddWhizbangPostgres_EntriesOverload_ConfigureOptions_InvokedAndReflectedInRegisteredOptionsAsync() {
    var services = new ServiceCollection();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var invoked = false;
    PostgresOptions? configuredInstance = null;

    services.AddWhizbangPostgres(
      _connectionString!, jsonOptions, initializeSchema: false, _emptyEntries,
      configureOptions: options => {
        invoked = true;
        configuredInstance = options;
        options.CommandTimeoutSeconds = 42;
        options.MaxInFlightCommands = 7;
      });

    await Assert.That(invoked).IsTrue()
      .Because("The configureOptions callback must run during registration, before the connection wait.");

    await using var provider = services.BuildServiceProvider();
    var resolved = provider.GetRequiredService<PostgresOptions>();

    await Assert.That(resolved).IsSameReferenceAs(configuredInstance)
      .Because("The exact PostgresOptions instance passed to configureOptions must be the registered singleton.");
    await Assert.That(resolved.CommandTimeoutSeconds).IsEqualTo(42);
    await Assert.That(resolved.MaxInFlightCommands).IsEqualTo(7);
  }

  [Test]
  public async Task AddWhizbangPostgres_EntriesOverload_InitializeSchemaTrue_CreatesSchemaAndRegistersReconciliationHostedServiceAsync() {
    var services = new ServiceCollection();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

    services.AddWhizbangPostgres(
      _connectionString!, jsonOptions, initializeSchema: true, _perspectiveEntries, configureOptions: null);

    // Schema was initialized eagerly: infrastructure tables + the per-perspective entry.
    await Assert.That(await _tableExistsAsync("wh_event_store")).IsTrue();
    await Assert.That(await _tableExistsAsync("wh_message_type_registry")).IsTrue();
    await Assert.That(await _tableExistsAsync("wh_per_registration_coverage")).IsTrue()
      .Because("perspectiveEntries SQL must be executed by the per-perspective hash-tracking initializer.");

    // initializeSchema: true must register the reconciliation hosted service.
    var reconciliationHostedService = services.FirstOrDefault(d =>
      d.ServiceType == typeof(IHostedService) &&
      d.ImplementationType == typeof(MessageTypeRegistryReconciliationHostedService));
    await Assert.That(reconciliationHostedService).IsNotNull();
    await Assert.That(reconciliationHostedService!.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
  }

  [Test]
  public async Task AddWhizbangPostgres_SchemaSqlOverload_ConfigureOptions_ResolvesFactoryRegisteredServicesAsync() {
    var services = new ServiceCollection();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var invoked = false;

    services.AddWhizbangPostgres(
      _connectionString!, jsonOptions, initializeSchema: false, perspectiveSchemaSql: null,
      configureOptions: options => {
        invoked = true;
        options.CommandTimeoutSeconds = 33;
      });

    await Assert.That(invoked).IsTrue();

    await using var provider = services.BuildServiceProvider();

    await Assert.That(provider.GetRequiredService<PostgresOptions>().CommandTimeoutSeconds).IsEqualTo(33);
    await Assert.That(provider.GetRequiredService<JsonSerializerOptions>()).IsSameReferenceAs(jsonOptions);

    // Each of these resolutions executes a factory lambda in the schemaSql overload body.
    await Assert.That(provider.GetRequiredService<IDbConnectionFactory>()).IsTypeOf<PostgresConnectionFactory>();
    await Assert.That(provider.GetRequiredService<IWorkCoordinator>()).IsTypeOf<DapperWorkCoordinator>();
    await Assert.That(provider.GetRequiredService<IRequestResponseStore>()).IsTypeOf<DapperPostgresRequestResponseStore>();
    await Assert.That(provider.GetRequiredService<ISequenceProvider>()).IsTypeOf<DapperPostgresSequenceProvider>();
    await Assert.That(provider.GetRequiredService<IPerspectiveSnapshotStore>()).IsTypeOf<DapperPerspectiveSnapshotStore>();
    await Assert.That(provider.GetRequiredService<IPerspectiveStreamLocker>()).IsTypeOf<DapperPerspectiveStreamLocker>();
    await Assert.That(provider.GetRequiredService<IPerspectiveCheckpointCompleter>()).IsTypeOf<DapperPostgresPerspectiveCheckpointCompleter>();
    await Assert.That(provider.GetRequiredService<IDeadLetterStore>()).IsTypeOf<DapperDeadLetterStore>();

    // Lifetimes on the schemaSql overload mirror the entries overload.
    await Assert.That(services.Single(d => d.ServiceType == typeof(IEventStore)).Lifetime).IsEqualTo(ServiceLifetime.Scoped);
    await Assert.That(services.Single(d => d.ServiceType == typeof(IWorkCoordinator)).Lifetime).IsEqualTo(ServiceLifetime.Singleton);
  }

  [Test]
  public async Task AddWhizbangPostgres_SchemaSqlOverload_InitializeSchemaTrue_WithConfigureOptions_CreatesSchemaAndRegistersHostedServiceAsync() {
    var services = new ServiceCollection();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    const string perspectiveSql = @"
      CREATE TABLE IF NOT EXISTS wh_per_schema_sql_coverage (
        id UUID PRIMARY KEY,
        model_data JSONB NOT NULL
      );";

    services.AddWhizbangPostgres(
      _connectionString!, jsonOptions, initializeSchema: true, perspectiveSchemaSql: perspectiveSql,
      configureOptions: options => options.InitialRetryAttempts = 3);

    await Assert.That(await _tableExistsAsync("wh_event_store")).IsTrue();
    await Assert.That(await _tableExistsAsync("wh_per_schema_sql_coverage")).IsTrue();

    var reconciliationHostedService = services.FirstOrDefault(d =>
      d.ServiceType == typeof(IHostedService) &&
      d.ImplementationType == typeof(MessageTypeRegistryReconciliationHostedService));
    await Assert.That(reconciliationHostedService).IsNotNull();

    await using var provider = services.BuildServiceProvider();
    await Assert.That(provider.GetRequiredService<PostgresOptions>().InitialRetryAttempts).IsEqualTo(3);
  }

  private static ServiceDescriptor _single(IServiceCollection services, Type serviceType) =>
    services.Single(d => d.ServiceType == serviceType);

  private async Task<bool> _tableExistsAsync(string tableName) {
    await using var connection = new NpgsqlConnection(_connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = @"
      SELECT EXISTS (
        SELECT FROM information_schema.tables
        WHERE table_schema = 'public'
        AND table_name = @tableName
      );";
    command.Parameters.AddWithValue(nameof(tableName), tableName);
    return (bool)(await command.ExecuteScalarAsync())!;
  }

  private sealed class EmptyMessageTypeCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [];
  }
}
