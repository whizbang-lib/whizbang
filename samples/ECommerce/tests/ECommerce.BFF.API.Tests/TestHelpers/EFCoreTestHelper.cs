using ECommerce.BFF.API.Hubs;
using ECommerce.BFF.API.Lenses;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres;

namespace ECommerce.BFF.API.Tests.TestHelpers;

/// <summary>
/// Helper class for setting up EF Core infrastructure with Whizbang unified API for testing.
/// Provides IPerspectiveStore and ILensQuery instances configured with PostgreSQL via Testcontainers.
/// </summary>
public sealed class EFCoreTestHelper : IAsyncDisposable {
  private readonly ServiceProvider _serviceProvider;
  private readonly BffDbContext _dbContext;
  private readonly PostgreSqlContainer _postgresContainer;

  public EFCoreTestHelper() {
    // Create and start PostgreSQL container
    _postgresContainer = new PostgreSqlBuilder()
      .WithImage("postgres:17-alpine")
      .WithDatabase($"bff_test_{Guid.CreateVersion7():N}")
      .Build();

    _postgresContainer.StartAsync().GetAwaiter().GetResult();

    var services = new ServiceCollection();

    // Add logging
    services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
    services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

    // Add DbContext with PostgreSQL
    var connectionString = _postgresContainer.GetConnectionString();
    services.AddDbContext<BffDbContext>(options => {
      options.UseNpgsql(connectionString);
    });

    // Register unified Whizbang API with EF Core Postgres driver
    _ = services
      .AddWhizbang()
      .WithEFCore<BffDbContext>()
      .WithDriver.Postgres;

    // Add simple mock for SignalR hub context
    services.AddSingleton<IHubContext<ProductInventoryHub>>(new TestHubContext());

    _serviceProvider = services.BuildServiceProvider();
    _dbContext = _serviceProvider.GetRequiredService<BffDbContext>();

    // Ensure database schema is created
    _dbContext.Database.EnsureCreated();
  }

  /// <summary>
  /// Gets an IPerspectiveStore for the specified model type.
  /// </summary>
  public IPerspectiveStore<TModel> GetPerspectiveStore<TModel>() where TModel : class {
    return _serviceProvider.GetRequiredService<IPerspectiveStore<TModel>>();
  }

  /// <summary>
  /// Gets an ILensQuery for the specified model type.
  /// </summary>
  public ILensQuery<TModel> GetLensQuery<TModel>() where TModel : class {
    return _serviceProvider.GetRequiredService<ILensQuery<TModel>>();
  }

  /// <summary>
  /// Gets the mock IHubContext for testing SignalR notifications.
  /// </summary>
  public IHubContext<ProductInventoryHub> GetHubContext() {
    return _serviceProvider.GetRequiredService<IHubContext<ProductInventoryHub>>();
  }

  /// <summary>
  /// Gets an ILogger for the specified type.
  /// </summary>
  public ILogger<T> GetLogger<T>() {
    return _serviceProvider.GetRequiredService<ILogger<T>>();
  }

  /// <summary>
  /// Cleans up the database by removing all data.
  /// </summary>
  public async Task CleanupDatabaseAsync(CancellationToken cancellationToken = default) {
    // Get all DbSet properties and clear them
    var dbSets = _dbContext.GetType()
      .GetProperties()
      .Where(p => p.PropertyType.IsGenericType &&
                  p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>));

    foreach (var dbSetProperty in dbSets) {
      var dbSet = dbSetProperty.GetValue(_dbContext);
      if (dbSet != null) {
        var removeRangeMethod = dbSetProperty.PropertyType.GetMethod("RemoveRange", new[] { typeof(System.Collections.IEnumerable) });
        if (removeRangeMethod != null) {
          var entityType = dbSetProperty.PropertyType.GetGenericArguments()[0];
          var localProperty = dbSetProperty.PropertyType.GetProperty("Local");
          if (localProperty != null) {
            var local = localProperty.GetValue(dbSet);
            if (local != null) {
              var toListMethod = local.GetType().GetMethod("ToList");
              if (toListMethod != null) {
                var entities = toListMethod.Invoke(local, null);
                if (entities != null) {
                  removeRangeMethod.Invoke(dbSet, new[] { entities });
                }
              }
            }
          }
        }
      }
    }

    await _dbContext.SaveChangesAsync(cancellationToken);
  }

  public async ValueTask DisposeAsync() {
    await _dbContext.DisposeAsync();
    await _serviceProvider.DisposeAsync();
    await _postgresContainer.DisposeAsync();
  }
}

/// <summary>
/// Simple test double for IHubContext that does nothing.
/// SignalR notifications are not the focus of perspective/lens tests.
/// </summary>
internal class TestHubContext : IHubContext<ProductInventoryHub> {
  public IHubClients Clients { get; } = new TestHubClients();
  public IGroupManager Groups { get; } = new TestGroupManager();
}

internal class TestHubClients : IHubClients {
  private readonly TestClientProxy _clientProxy = new();

  public IClientProxy All => _clientProxy;
  public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _clientProxy;
  public IClientProxy Client(string connectionId) => _clientProxy;
  public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _clientProxy;
  public IClientProxy Group(string groupName) => _clientProxy;
  public IClientProxy Groups(IReadOnlyList<string> groupNames) => _clientProxy;
  public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _clientProxy;
  public IClientProxy User(string userId) => _clientProxy;
  public IClientProxy Users(IReadOnlyList<string> userIds) => _clientProxy;
}

internal class TestClientProxy : IClientProxy {
  public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) {
    // Do nothing - SignalR notifications are not the focus of these tests
    return Task.CompletedTask;
  }
}

internal class TestGroupManager : IGroupManager {
  public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) {
    // Do nothing - SignalR group management is not the focus of these tests
    return Task.CompletedTask;
  }

  public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) {
    // Do nothing - SignalR group management is not the focus of these tests
    return Task.CompletedTask;
  }
}
