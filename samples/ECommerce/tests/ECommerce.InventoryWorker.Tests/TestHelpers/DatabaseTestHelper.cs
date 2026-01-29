using System.Diagnostics.CodeAnalysis;
using Dapper;
using ECommerce.Contracts.Generated;
using ECommerce.InventoryWorker;
using ECommerce.InventoryWorker.Generated;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres;
using Whizbang.Testing.Containers;

namespace ECommerce.InventoryWorker.Tests.TestHelpers;

/// <summary>
/// Helper class for managing PostgreSQL test databases via SharedPostgresContainer for InventoryWorker tests.
/// Provides DbContext, ILensQuery, and IPerspectiveStore instances for testing.
/// </summary>
public sealed class DatabaseTestHelper : IAsyncDisposable {
  private string? _fixtureDatabaseName;  // Unique database name for this fixture instance
  private string? _connectionString;  // Connection string pointing to the fixture's unique database
  private bool _isInitialized;
  private IServiceProvider? _serviceProvider;

  public DatabaseTestHelper() {
    Console.WriteLine("[DatabaseTestHelper] Using SharedPostgresContainer for PostgreSQL");
  }

  /// <summary>
  /// Creates and returns a configured IServiceProvider with all EF Core infrastructure registered.
  /// </summary>
  [RequiresDynamicCode("EF Core in tests may use dynamic code")]
  [RequiresUnreferencedCode("EF Core in tests may use unreferenced code")]
  public async Task<IServiceProvider> CreateServiceProviderAsync(CancellationToken cancellationToken = default) {
    if (!_isInitialized) {
      // Initialize SharedPostgresContainer
      await SharedPostgresContainer.InitializeAsync(cancellationToken);

      // Create a unique database for this fixture instance
      _fixtureDatabaseName = $"fixture_{Guid.NewGuid():N}";
      Console.WriteLine($"[DatabaseTestHelper] Creating unique database: {_fixtureDatabaseName}");

      await using (var conn = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync($"CREATE DATABASE \"{_fixtureDatabaseName}\"");
      }

      // Build connection string with the new database name
      var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
        Database = _fixtureDatabaseName
      };
      _connectionString = builder.ConnectionString;

      var services = new ServiceCollection();

      // Register JsonSerializerOptions for Npgsql JSONB serialization
      var jsonOptions = ECommerce.Contracts.Generated.WhizbangJsonContext.CreateOptions();
      services.AddSingleton(jsonOptions);

      // Register DbContext with NpgsqlDataSource
      // IMPORTANT: ConfigureJsonOptions() MUST be called BEFORE EnableDynamicJson() (Npgsql bug #5562)
      // This registers WhizbangId JSON converters for JSONB serialization
      var dataSourceBuilder = new NpgsqlDataSourceBuilder(_connectionString);
      dataSourceBuilder.ConfigureJsonOptions(jsonOptions);
      dataSourceBuilder.EnableDynamicJson();
      var dataSource = dataSourceBuilder.Build();
      services.AddSingleton(dataSource);

      services.AddDbContext<InventoryDbContext>(options =>
        options.UseNpgsql(dataSource));

      // Register Whizbang infrastructure with EF Core + Postgres driver
      _ = services.AddWhizbang()
        .WithEFCore<InventoryDbContext>()
        .WithDriver.Postgres;

      // NOTE: InventoryWorker has NO perspectives (OLD OrderInventoryPerspective was removed)
      // If perspectives are added in the future, call services.AddPerspectiveRunners()

      // Register lenses (high-level query interfaces)
      services.AddScoped<ECommerce.InventoryWorker.Lenses.IProductLens, ECommerce.InventoryWorker.Lenses.ProductLens>();
      services.AddScoped<ECommerce.InventoryWorker.Lenses.IInventoryLens, ECommerce.InventoryWorker.Lenses.InventoryLens>();

      // Register generated services (from ECommerce.Contracts)
      services.AddReceptors();
      services.AddWhizbangAggregateIdExtractor();
      services.AddWhizbangDispatcher();

      // Add NullLogger for all logger dependencies
      services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

      // Register JsonSerializerOptions for Npgsql JSONB serialization
      services.AddSingleton(ECommerce.Contracts.Generated.WhizbangJsonContext.CreateOptions());

      _serviceProvider = services.BuildServiceProvider();

      // Initialize schema (creates tables + PostgreSQL functions)
      await using var scope = _serviceProvider.CreateAsyncScope();
      var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
      await dbContext.EnsureWhizbangDatabaseInitializedAsync();

      _isInitialized = true;
    }

    return _serviceProvider!;
  }

  /// <summary>
  /// Gets the connection string for raw SQL operations if needed.
  /// </summary>
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task<string> GetConnectionStringAsync(CancellationToken cancellationToken = default) {
    if (!_isInitialized) {
      await CreateServiceProviderAsync(cancellationToken);
    }
    return _connectionString ?? throw new InvalidOperationException("DatabaseTestHelper not initialized");
  }

  /// <summary>
  /// Cleans up all test data from the database.
  /// </summary>
  public async Task CleanupDatabaseAsync(CancellationToken cancellationToken = default) {
    if (!_isInitialized || _serviceProvider == null) {
      return;
    }

    // Temporarily disabled to see actual test errors without cleanup noise
    // await using var scope = _serviceProvider.CreateAsyncScope();
    // var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

    // // Truncate all perspective tables
    // await dbContext.Database.ExecuteSqlRawAsync(@"
    //   TRUNCATE TABLE wh_per_product_dto CASCADE;
    //   TRUNCATE TABLE wh_per_inventory_level_dto CASCADE;
    //   TRUNCATE TABLE wh_outbox CASCADE;
    //   TRUNCATE TABLE wh_inbox CASCADE;
    //   TRUNCATE TABLE wh_event_store CASCADE;
    // ", cancellationToken);
    await Task.CompletedTask;
  }

  public async ValueTask DisposeAsync() {
    if (_serviceProvider is IAsyncDisposable asyncDisposable) {
      await asyncDisposable.DisposeAsync();
    }

    // Drop the fixture's unique database from SharedPostgresContainer
    // The container itself is NOT disposed - it's shared across all tests
    if (_fixtureDatabaseName != null) {
      try {
        await using var conn = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await conn.OpenAsync();
        // Terminate any remaining connections to the database before dropping
        await conn.ExecuteAsync($@"
          SELECT pg_terminate_backend(pid)
          FROM pg_stat_activity
          WHERE datname = '{_fixtureDatabaseName}' AND pid <> pg_backend_pid()");
        await conn.ExecuteAsync($"DROP DATABASE IF EXISTS \"{_fixtureDatabaseName}\"");
        Console.WriteLine($"[DatabaseTestHelper] Dropped database: {_fixtureDatabaseName}");
      } catch (Exception ex) {
        Console.WriteLine($"[DatabaseTestHelper] Warning: Failed to drop database {_fixtureDatabaseName}: {ex.Message}");
      }
    }
  }
}
