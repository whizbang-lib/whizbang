using ECommerce.BFF.API;
using ECommerce.BFF.API.Generated;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Data.EFCore.Postgres;

namespace ECommerce.BFF.API.Tests.TestHelpers;

/// <summary>
/// Helper class for managing PostgreSQL test containers and EF Core infrastructure for BFF.API tests.
/// Provides DbContext and all EF Core infrastructure for testing.
/// </summary>
public sealed class DatabaseTestHelper : IAsyncDisposable {
  private readonly PostgreSqlContainer _container;
  private bool _isInitialized;
  private IServiceProvider? _serviceProvider;

  public DatabaseTestHelper() {
    _container = new PostgreSqlBuilder()
      .WithImage("postgres:17-alpine")
      .WithDatabase("whizbang_test")
      .WithUsername("whizbang_user")
      .WithPassword("whizbang_pass")
      .Build();
  }

  /// <summary>
  /// Creates and returns a configured IServiceProvider with all EF Core infrastructure registered.
  /// </summary>
  public async Task<IServiceProvider> CreateServiceProviderAsync(CancellationToken cancellationToken = default) {
    if (!_isInitialized) {
      await _container.StartAsync(cancellationToken);
      var connectionString = _container.GetConnectionString();

      var services = new ServiceCollection();

      // Register DbContext
      services.AddDbContext<BffDbContext>(options =>
        options.UseNpgsql(connectionString));

      // Register Whizbang infrastructure with EF Core + Postgres driver
      _ = services.AddWhizbang()
        .WithEFCore<BffDbContext>()
        .WithDriver.Postgres;

      // Register generated services (perspectives, lenses)
      services.AddReceptors();
      services.AddWhizbangDispatcher();

      // Add NullLogger for all logger dependencies
      services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

      _serviceProvider = services.BuildServiceProvider();

      // Initialize schema (creates tables + PostgreSQL functions)
      await using var scope = _serviceProvider.CreateAsyncScope();
      var dbContext = scope.ServiceProvider.GetRequiredService<BffDbContext>();
      await dbContext.EnsureWhizbangDatabaseInitializedAsync();

      _isInitialized = true;
    }

    return _serviceProvider!;
  }

  /// <summary>
  /// Gets the connection string for raw SQL operations if needed.
  /// </summary>
  public async Task<string> GetConnectionStringAsync(CancellationToken cancellationToken = default) {
    if (!_isInitialized) {
      await CreateServiceProviderAsync(cancellationToken);
    }
    return _container.GetConnectionString();
  }

  /// <summary>
  /// Cleans up all test data from the database.
  /// </summary>
  public async Task CleanupDatabaseAsync(CancellationToken cancellationToken = default) {
    if (!_isInitialized || _serviceProvider == null) {
      return;
    }

    await using var scope = _serviceProvider.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<BffDbContext>();

    // Truncate all perspective tables
    await dbContext.Database.ExecuteSqlRawAsync(@"
      TRUNCATE TABLE wh_per_product_dto CASCADE;
      TRUNCATE TABLE wh_per_inventory_level_dto CASCADE;
      TRUNCATE TABLE wh_per_order_summary_dto CASCADE;
      TRUNCATE TABLE wh_outbox CASCADE;
      TRUNCATE TABLE wh_inbox CASCADE;
      TRUNCATE TABLE wh_event_store CASCADE;
    ", cancellationToken);
  }

  public async ValueTask DisposeAsync() {
    if (_serviceProvider is IAsyncDisposable asyncDisposable) {
      await asyncDisposable.DisposeAsync();
    }

    if (_isInitialized) {
      await _container.DisposeAsync();
    }
  }
}
