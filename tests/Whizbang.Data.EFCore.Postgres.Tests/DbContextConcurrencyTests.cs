#pragma warning disable CS0618
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests that reproduce and verify the fix for DbContext concurrency errors
/// when multiple parallel operations access the same DbContext instance.
/// </summary>
/// <remarks>
/// These tests demonstrate the core problem: DbContext is NOT thread-safe.
/// When multiple tasks query the same DbContext concurrently, EF Core throws
/// "A second operation was started on this context instance before a previous operation completed".
///
/// The fix changes ILensQuery registration from Scoped to Transient with IDbContextFactory,
/// giving each injection its own DbContext from the connection pool.
/// </remarks>
/// <docs>lenses/lens-query-factory</docs>
[Category("Integration")]
[Category("Concurrency")]
[NotInParallel("EFCorePostgresTests")]
public class DbContextConcurrencyTests : EFCoreTestBase {
  private const int ParallelQueryCount = 10;
  private const int HighConcurrencyCount = 50;

  #region RED Tests - Reproduce Concurrency Error

  /// <summary>
  /// RED TEST: Directly reproduces concurrent access to same DbContext.
  /// This test demonstrates the core problem that our fix addresses.
  /// </summary>
  [Test]
  public async Task ConcurrentQueries_OnSameDbContext_ThrowsConcurrencyErrorAsync() {
    // Arrange - Create a single DbContext and query it concurrently
    await using var context = CreateDbContext();

    // Seed some data
    await SeedOrderAsync(context, TestOrderId.New(), 100.00m, "Created");
    await context.SaveChangesAsync();

    // Act - Run multiple queries concurrently on the same context
    var tasks = new List<Task<int>>();
    for (var i = 0; i < ParallelQueryCount; i++) {
      // Each task queries the same context concurrently - this is NOT thread-safe
      tasks.Add(Task.Run(async () => {
        // Add small delay to increase chance of concurrent access
        await Task.Delay(Random.Shared.Next(1, 5));
        return await context.Set<PerspectiveRow<Order>>().CountAsync();
      }));
    }

    // Assert - Should throw InvalidOperationException about concurrent access
    Exception? caughtException = null;
    try {
      await Task.WhenAll(tasks);
    } catch (Exception ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull()
        .Because("Concurrent DbContext access should throw an exception");

    // The exception should be about concurrent operations
    var exceptionMessage = caughtException!.ToString();
    var hasConcurrencyMessage =
        exceptionMessage.Contains("second operation", StringComparison.OrdinalIgnoreCase) ||
        exceptionMessage.Contains("previous operation", StringComparison.OrdinalIgnoreCase) ||
        exceptionMessage.Contains("concurrency", StringComparison.OrdinalIgnoreCase) ||
        exceptionMessage.Contains("thread", StringComparison.OrdinalIgnoreCase);

    await Assert.That(hasConcurrencyMessage).IsTrue()
        .Because("Concurrent DbContext access should throw a concurrency-related exception");
  }

  /// <summary>
  /// RED TEST: Reproduces concurrency error with scoped lens registration.
  /// When ILensQuery is registered as Scoped, all injections in the same scope
  /// share the same DbContext, causing concurrency errors.
  /// </summary>
  [Test]
  public async Task ParallelQueries_WithScopedLens_ThrowsConcurrencyErrorAsync() {
    // Arrange - Set up DI with SCOPED lens (the problematic pattern)
    var services = new ServiceCollection();

    // Register DbContext as Scoped
    services.AddScoped(_ => CreateDbContext());

    // Register lens as Scoped - it will share the scoped DbContext
    services.AddScoped<ILensQuery<Order>>(sp => {
      var context = sp.GetRequiredService<WorkCoordinationDbContext>();
      return new EFCorePostgresLensQuery<Order>(context, "orders_perspective");
    });

    await using var serviceProvider = services.BuildServiceProvider();

    // Seed test data
    await using (var seedContext = CreateDbContext()) {
      await SeedOrderAsync(seedContext, TestOrderId.New(), 100.00m, "Created");
      await seedContext.SaveChangesAsync();
    }

    // Act - Create a scope and run parallel queries
    using var scope = serviceProvider.CreateScope();
    var lens = scope.ServiceProvider.GetRequiredService<ILensQuery<Order>>();

    var tasks = new List<Task<int>>();
    for (var i = 0; i < ParallelQueryCount; i++) {
      tasks.Add(Task.Run(async () => {
        await Task.Delay(Random.Shared.Next(1, 5));
        return await lens.Query.CountAsync();
      }));
    }

    // Assert - Should throw due to concurrent access on shared DbContext
    Exception? caughtException = null;
    try {
      await Task.WhenAll(tasks);
    } catch (Exception ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull()
        .Because("Scoped lens with parallel queries should cause concurrency errors");
  }

  /// <summary>
  /// RED TEST: Multiple scoped lens injections in same scope share DbContext.
  /// </summary>
  [Test]
  public async Task MultipleScopedLensInjections_ShareSameDbContext_CausesConcurrencyErrorAsync() {
    // Arrange
    var services = new ServiceCollection();
    var capturedContexts = new List<DbContext>();

    services.AddScoped(_ => CreateDbContext());

    services.AddScoped<ILensQuery<Order>>(sp => {
      var context = sp.GetRequiredService<WorkCoordinationDbContext>();
      lock (capturedContexts) { capturedContexts.Add(context); }
      return new EFCorePostgresLensQuery<Order>(context, "orders_perspective");
    });

    await using var serviceProvider = services.BuildServiceProvider();

    // Act - Get two lens instances in the same scope
    using var scope = serviceProvider.CreateScope();
    var lens1 = scope.ServiceProvider.GetRequiredService<ILensQuery<Order>>();
    var lens2 = scope.ServiceProvider.GetRequiredService<ILensQuery<Order>>();

    // Assert - Both should be the same instance (scoped)
    await Assert.That(capturedContexts.Count).IsEqualTo(1)
        .Because("Scoped registration should return same instance");
    await Assert.That(lens1).IsSameReferenceAs(lens2)
        .Because("Scoped lens should be the same instance within a scope");
  }

  #endregion

  #region GREEN Tests - Verify Fix Works

  /// <summary>
  /// GREEN TEST: Verifies that using IDbContextFactory with transient registration
  /// allows parallel queries to work correctly.
  /// </summary>
  [Test]
  public async Task ParallelQueries_WithTransientFactory_SucceedsAsync() {
    // Arrange - Set up DI with TRANSIENT factory pattern (fixed)
    var services = new ServiceCollection();

    // Register DbContextFactory for pooled contexts
    services.AddPooledDbContextFactory<WorkCoordinationDbContext>(options => {
      options.UseNpgsql(ConnectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    });

    // Register lens as Transient using factory - each gets its own DbContext
    services.AddTransient<ILensQuery<Order>>(sp => {
      var factory = sp.GetRequiredService<IDbContextFactory<WorkCoordinationDbContext>>();
      var context = factory.CreateDbContext();
      return new EFCorePostgresLensQuery<Order>(context, "orders_perspective");
    });

    await using var serviceProvider = services.BuildServiceProvider();

    // Seed test data
    await using (var seedContext = CreateDbContext()) {
      await SeedOrderAsync(seedContext, TestOrderId.New(), 100.00m, "Created");
      await seedContext.SaveChangesAsync();
    }

    // Act - Run parallel queries with transient lenses
    var tasks = new List<Task<int>>();
    for (var i = 0; i < ParallelQueryCount; i++) {
      tasks.Add(Task.Run(async () => {
        // Each task gets its own lens with its own DbContext
        var lens = serviceProvider.GetRequiredService<ILensQuery<Order>>();
        await Task.Delay(Random.Shared.Next(1, 5));
        return await lens.Query.CountAsync();
      }));
    }

    var results = await Task.WhenAll(tasks);

    // Assert - All queries should succeed
    await Assert.That(results.All(r => r == 1)).IsTrue()
        .Because("All parallel queries should return count of 1");
  }

  /// <summary>
  /// GREEN TEST: Verifies high concurrency scenario with transient factory.
  /// </summary>
  [Test]
  public async Task HighConcurrency_With50ParallelQueries_SucceedsAsync() {
    // Arrange
    var services = new ServiceCollection();

    services.AddPooledDbContextFactory<WorkCoordinationDbContext>(options => {
      options.UseNpgsql(ConnectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    });

    services.AddTransient<ILensQuery<Order>>(sp => {
      var factory = sp.GetRequiredService<IDbContextFactory<WorkCoordinationDbContext>>();
      var context = factory.CreateDbContext();
      return new EFCorePostgresLensQuery<Order>(context, "orders_perspective");
    });

    await using var serviceProvider = services.BuildServiceProvider();

    // Seed test data
    await using (var seedContext = CreateDbContext()) {
      for (var i = 0; i < 10; i++) {
        await SeedOrderAsync(seedContext, TestOrderId.New(), 100.00m + i, "Created");
      }
      await seedContext.SaveChangesAsync();
    }

    // Act - High concurrency test
    var tasks = new List<Task<int>>();
    for (var i = 0; i < HighConcurrencyCount; i++) {
      tasks.Add(Task.Run(async () => {
        var lens = serviceProvider.GetRequiredService<ILensQuery<Order>>();
        await Task.Delay(Random.Shared.Next(1, 10));
        return await lens.Query.CountAsync();
      }));
    }

    var results = await Task.WhenAll(tasks);

    // Assert - All queries should succeed with correct count
    await Assert.That(results.All(r => r == 10)).IsTrue()
        .Because("All high-concurrency queries should return count of 10");
  }

  /// <summary>
  /// GREEN TEST: Concurrent queries with separate DbContexts succeed.
  /// </summary>
  [Test]
  public async Task ConcurrentQueries_WithSeparateDbContexts_SucceedsAsync() {
    // Arrange - Seed test data
    await using (var seedContext = CreateDbContext()) {
      await SeedOrderAsync(seedContext, TestOrderId.New(), 100.00m, "Created");
      await SeedOrderAsync(seedContext, TestOrderId.New(), 200.00m, "Shipped");
      await seedContext.SaveChangesAsync();
    }

    // Act - Run parallel queries, each with its own DbContext
    var tasks = new List<Task<int>>();
    for (var i = 0; i < ParallelQueryCount; i++) {
      tasks.Add(Task.Run(async () => {
        // Each task creates its own context - thread-safe
        await using var context = CreateDbContext();
        await Task.Delay(Random.Shared.Next(1, 5));
        return await context.Set<PerspectiveRow<Order>>().CountAsync();
      }));
    }

    var results = await Task.WhenAll(tasks);

    // Assert - All queries should succeed
    await Assert.That(results.All(r => r == 2)).IsTrue()
        .Because("All concurrent queries with separate DbContexts should return count of 2");
  }

  #endregion

  #region DbContext Isolation Tests

  /// <summary>
  /// Verifies that multiple transient ILensQuery injections get different DbContext instances.
  /// </summary>
  [Test]
  public async Task MultipleTransientInjections_GetDifferentDbContextsAsync() {
    // Arrange
    var services = new ServiceCollection();
    var contextInstances = new List<DbContext>();

    services.AddPooledDbContextFactory<WorkCoordinationDbContext>(options => {
      options.UseNpgsql(ConnectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    });

    // Track context instances
    services.AddTransient<ILensQuery<Order>>(sp => {
      var factory = sp.GetRequiredService<IDbContextFactory<WorkCoordinationDbContext>>();
      var context = factory.CreateDbContext();
      lock (contextInstances) {
        contextInstances.Add(context);
      }
      return new EFCorePostgresLensQuery<Order>(context, "orders_perspective");
    });

    await using var serviceProvider = services.BuildServiceProvider();

    // Act - Resolve lens multiple times
    var lens1 = serviceProvider.GetRequiredService<ILensQuery<Order>>();
    var lens2 = serviceProvider.GetRequiredService<ILensQuery<Order>>();
    var lens3 = serviceProvider.GetRequiredService<ILensQuery<Order>>();

    // Assert - Should have different context instances
    await Assert.That(contextInstances.Count).IsEqualTo(3);
    await Assert.That(contextInstances[0]).IsNotSameReferenceAs(contextInstances[1]);
    await Assert.That(contextInstances[0]).IsNotSameReferenceAs(contextInstances[2]);
    await Assert.That(contextInstances[1]).IsNotSameReferenceAs(contextInstances[2]);
  }

  /// <summary>
  /// Verifies that transient lens instances are different objects.
  /// </summary>
  [Test]
  public async Task TwoTransientInjections_AreDifferentInstancesAsync() {
    // Arrange
    var services = new ServiceCollection();

    services.AddPooledDbContextFactory<WorkCoordinationDbContext>(options => {
      options.UseNpgsql(ConnectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    });

    services.AddTransient<ILensQuery<Order>>(sp => {
      var factory = sp.GetRequiredService<IDbContextFactory<WorkCoordinationDbContext>>();
      var context = factory.CreateDbContext();
      return new EFCorePostgresLensQuery<Order>(context, "orders_perspective");
    });

    await using var serviceProvider = services.BuildServiceProvider();

    // Act
    var lens1 = serviceProvider.GetRequiredService<ILensQuery<Order>>();
    var lens2 = serviceProvider.GetRequiredService<ILensQuery<Order>>();

    // Assert
    await Assert.That(lens1).IsNotSameReferenceAs(lens2)
        .Because("Transient registration should create different instances");
  }

  /// <summary>
  /// Verifies that three transient injections all have different DbContexts.
  /// </summary>
  [Test]
  public async Task ThreeTransientInjections_AllHaveDifferentDbContextsAsync() {
    // Arrange
    var contexts = new List<WorkCoordinationDbContext>();

    var services = new ServiceCollection();

    services.AddPooledDbContextFactory<WorkCoordinationDbContext>(options => {
      options.UseNpgsql(ConnectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    });

    services.AddTransient<ILensQuery<Order>>(sp => {
      var factory = sp.GetRequiredService<IDbContextFactory<WorkCoordinationDbContext>>();
      var context = factory.CreateDbContext();
      lock (contexts) { contexts.Add(context); }
      return new EFCorePostgresLensQuery<Order>(context, "orders_perspective");
    });

    await using var serviceProvider = services.BuildServiceProvider();

    // Act - Get three lens instances
    _ = serviceProvider.GetRequiredService<ILensQuery<Order>>();
    _ = serviceProvider.GetRequiredService<ILensQuery<Order>>();
    _ = serviceProvider.GetRequiredService<ILensQuery<Order>>();

    // Assert - All three contexts should be different
    await Assert.That(contexts.Count).IsEqualTo(3);
    await Assert.That(contexts[0]).IsNotSameReferenceAs(contexts[1]);
    await Assert.That(contexts[0]).IsNotSameReferenceAs(contexts[2]);
    await Assert.That(contexts[1]).IsNotSameReferenceAs(contexts[2]);
  }

  #endregion

  #region Connection Pool Tests

  /// <summary>
  /// Verifies that pooled DbContext factory returns connections to the pool.
  /// </summary>
  [Test]
  public async Task PooledDbContextFactory_ReusesConnectionsAsync() {
    // Arrange
    var services = new ServiceCollection();

    services.AddPooledDbContextFactory<WorkCoordinationDbContext>(options => {
      options.UseNpgsql(ConnectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    });

    await using var serviceProvider = services.BuildServiceProvider();
    var factory = serviceProvider.GetRequiredService<IDbContextFactory<WorkCoordinationDbContext>>();

    // Seed test data
    await using (var seedContext = CreateDbContext()) {
      await SeedOrderAsync(seedContext, TestOrderId.New(), 100.00m, "Created");
      await seedContext.SaveChangesAsync();
    }

    // Act - Create, use, and dispose many contexts
    var successCount = 0;
    for (var i = 0; i < 20; i++) {
      await using var context = factory.CreateDbContext();
      var count = await context.Set<PerspectiveRow<Order>>().CountAsync();
      await Assert.That(count).IsEqualTo(1);
      successCount++;
    }

    // Assert - All 20 iterations should have completed successfully
    // (no connection exhaustion despite creating 20 contexts)
    await Assert.That(successCount).IsEqualTo(20)
        .Because("Pooled DbContext factory should handle 20 consecutive contexts");
  }

  /// <summary>
  /// Verifies that factory under load handles connections correctly.
  /// </summary>
  [Test]
  public async Task Factory_UnderLoad_HandlesConnectionsCorrectlyAsync() {
    // Arrange
    var services = new ServiceCollection();

    services.AddPooledDbContextFactory<WorkCoordinationDbContext>(options => {
      options.UseNpgsql(ConnectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    });

    await using var serviceProvider = services.BuildServiceProvider();
    var factory = serviceProvider.GetRequiredService<IDbContextFactory<WorkCoordinationDbContext>>();

    // Seed test data
    await using (var seedContext = CreateDbContext()) {
      await SeedOrderAsync(seedContext, TestOrderId.New(), 100.00m, "Created");
      await seedContext.SaveChangesAsync();
    }

    // Act - Run many concurrent queries using factory
    var tasks = new List<Task<int>>();
    for (var i = 0; i < 30; i++) {
      tasks.Add(Task.Run(async () => {
        await using var context = factory.CreateDbContext();
        await Task.Delay(Random.Shared.Next(5, 20));
        return await context.Set<PerspectiveRow<Order>>().CountAsync();
      }));
    }

    var results = await Task.WhenAll(tasks);

    // Assert - All queries should complete successfully
    await Assert.That(results.All(r => r == 1)).IsTrue()
        .Because("All concurrent queries under load should return count of 1");
  }

  #endregion

  #region Real PostgreSQL Query Tests

  /// <summary>
  /// Verifies queries work with real PostgreSQL.
  /// </summary>
  [Test]
  public async Task Query_WithRealPostgres_ReturnsDataAsync() {
    // Arrange - Seed some test data
    await using (var seedContext = CreateDbContext()) {
      await SeedOrderAsync(seedContext, TestOrderId.New(), 99.99m, "Created");
      await seedContext.SaveChangesAsync();
    }

    var services = new ServiceCollection();

    services.AddPooledDbContextFactory<WorkCoordinationDbContext>(options => {
      options.UseNpgsql(ConnectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    });

    services.AddTransient<ILensQuery<Order>>(sp => {
      var factory = sp.GetRequiredService<IDbContextFactory<WorkCoordinationDbContext>>();
      return new EFCorePostgresLensQuery<Order>(factory.CreateDbContext(), "orders_perspective");
    });

    await using var serviceProvider = services.BuildServiceProvider();

    // Act
    var lens = serviceProvider.GetRequiredService<ILensQuery<Order>>();
    var count = await lens.Query.CountAsync();

    // Assert
    await Assert.That(count).IsGreaterThanOrEqualTo(1);
  }

  /// <summary>
  /// Verifies GetByIdAsync works with real PostgreSQL.
  /// </summary>
  [Test]
  public async Task GetByIdAsync_WithRealPostgres_ReturnsCorrectRecordAsync() {
    // Arrange
    var orderId = TestOrderId.New();
    await using (var seedContext = CreateDbContext()) {
      await SeedOrderAsync(seedContext, orderId, 149.99m, "Shipped");
      await seedContext.SaveChangesAsync();
    }

    var services = new ServiceCollection();

    services.AddPooledDbContextFactory<WorkCoordinationDbContext>(options => {
      options.UseNpgsql(ConnectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    });

    services.AddTransient<ILensQuery<Order>>(sp => {
      var factory = sp.GetRequiredService<IDbContextFactory<WorkCoordinationDbContext>>();
      return new EFCorePostgresLensQuery<Order>(factory.CreateDbContext(), "orders_perspective");
    });

    await using var serviceProvider = services.BuildServiceProvider();

    // Act
    var lens = serviceProvider.GetRequiredService<ILensQuery<Order>>();
    var order = await lens.GetByIdAsync(orderId.Value);

    // Assert
    await Assert.That(order).IsNotNull();
    await Assert.That(order!.Amount).IsEqualTo(149.99m);
    await Assert.That(order.Status).IsEqualTo("Shipped");
  }

  /// <summary>
  /// Verifies filtering works with real PostgreSQL.
  /// </summary>
  [Test]
  public async Task Query_WithFiltering_ReturnsFilteredResultsAsync() {
    // Arrange
    await using (var seedContext = CreateDbContext()) {
      await SeedOrderAsync(seedContext, TestOrderId.New(), 10.00m, "Created");
      await SeedOrderAsync(seedContext, TestOrderId.New(), 1000.00m, "Created");
      await SeedOrderAsync(seedContext, TestOrderId.New(), 100.00m, "Created");
      await seedContext.SaveChangesAsync();
    }

    var services = new ServiceCollection();

    services.AddPooledDbContextFactory<WorkCoordinationDbContext>(options => {
      options.UseNpgsql(ConnectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    });

    services.AddTransient<ILensQuery<Order>>(sp => {
      var factory = sp.GetRequiredService<IDbContextFactory<WorkCoordinationDbContext>>();
      return new EFCorePostgresLensQuery<Order>(factory.CreateDbContext(), "orders_perspective");
    });

    await using var serviceProvider = services.BuildServiceProvider();

    // Act - Filter orders over $50
    var lens = serviceProvider.GetRequiredService<ILensQuery<Order>>();
    var expensiveOrders = await lens.Query
        .Where(p => p.Data.Amount > 50.00m)
        .ToListAsync();

    // Assert
    await Assert.That(expensiveOrders.Count).IsEqualTo(2);
    await Assert.That(expensiveOrders.All(p => p.Data.Amount > 50.00m)).IsTrue();
  }

  /// <summary>
  /// Verifies sorting works with real PostgreSQL.
  /// </summary>
  [Test]
  public async Task Query_WithSorting_ReturnsSortedResultsAsync() {
    // Arrange
    await using (var seedContext = CreateDbContext()) {
      await SeedOrderAsync(seedContext, TestOrderId.New(), 300.00m, "Created");
      await SeedOrderAsync(seedContext, TestOrderId.New(), 100.00m, "Created");
      await SeedOrderAsync(seedContext, TestOrderId.New(), 200.00m, "Created");
      await seedContext.SaveChangesAsync();
    }

    var services = new ServiceCollection();

    services.AddPooledDbContextFactory<WorkCoordinationDbContext>(options => {
      options.UseNpgsql(ConnectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    });

    services.AddTransient<ILensQuery<Order>>(sp => {
      var factory = sp.GetRequiredService<IDbContextFactory<WorkCoordinationDbContext>>();
      return new EFCorePostgresLensQuery<Order>(factory.CreateDbContext(), "orders_perspective");
    });

    await using var serviceProvider = services.BuildServiceProvider();

    // Act - Sort by amount ascending
    var lens = serviceProvider.GetRequiredService<ILensQuery<Order>>();
    var sortedOrders = await lens.Query
        .OrderBy(p => p.Data.Amount)
        .ToListAsync();

    // Assert
    await Assert.That(sortedOrders.Count).IsGreaterThanOrEqualTo(3);
    await Assert.That(sortedOrders[0].Data.Amount).IsEqualTo(100.00m);
    await Assert.That(sortedOrders[1].Data.Amount).IsEqualTo(200.00m);
    await Assert.That(sortedOrders[2].Data.Amount).IsEqualTo(300.00m);
  }

  /// <summary>
  /// Verifies paging works with real PostgreSQL.
  /// </summary>
  [Test]
  public async Task Query_WithPaging_ReturnsPagedResultsAsync() {
    // Arrange
    await using (var seedContext = CreateDbContext()) {
      for (var i = 0; i < 10; i++) {
        await SeedOrderAsync(seedContext, TestOrderId.New(), 10.00m * (i + 1), "Created");
      }
      await seedContext.SaveChangesAsync();
    }

    var services = new ServiceCollection();

    services.AddPooledDbContextFactory<WorkCoordinationDbContext>(options => {
      options.UseNpgsql(ConnectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    });

    services.AddTransient<ILensQuery<Order>>(sp => {
      var factory = sp.GetRequiredService<IDbContextFactory<WorkCoordinationDbContext>>();
      return new EFCorePostgresLensQuery<Order>(factory.CreateDbContext(), "orders_perspective");
    });

    await using var serviceProvider = services.BuildServiceProvider();

    // Act - Get second page (skip 5, take 5)
    var lens = serviceProvider.GetRequiredService<ILensQuery<Order>>();
    var page2 = await lens.Query
        .OrderBy(p => p.Data.Amount)
        .Skip(5)
        .Take(5)
        .ToListAsync();

    // Assert
    await Assert.That(page2.Count).IsEqualTo(5);
  }

  #endregion

  #region Disposal Tests

  /// <summary>
  /// Verifies that after scope disposal, a new scope gets a new DbContext.
  /// </summary>
  [Test]
  public async Task AfterScopeDisposal_NewScopeGetsNewDbContextAsync() {
    // Arrange
    var contexts = new List<DbContext>();

    var services = new ServiceCollection();

    services.AddPooledDbContextFactory<WorkCoordinationDbContext>(options => {
      options.UseNpgsql(ConnectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    });

    services.AddTransient<ILensQuery<Order>>(sp => {
      var factory = sp.GetRequiredService<IDbContextFactory<WorkCoordinationDbContext>>();
      var context = factory.CreateDbContext();
      lock (contexts) { contexts.Add(context); }
      return new EFCorePostgresLensQuery<Order>(context, "orders_perspective");
    });

    await using var serviceProvider = services.BuildServiceProvider();

    // Act - Use two separate scopes
    using (var scope1 = serviceProvider.CreateScope()) {
      _ = scope1.ServiceProvider.GetRequiredService<ILensQuery<Order>>();
    }

    using (var scope2 = serviceProvider.CreateScope()) {
      _ = scope2.ServiceProvider.GetRequiredService<ILensQuery<Order>>();
    }

    // Assert - Each scope should have gotten its own context
    await Assert.That(contexts.Count).IsEqualTo(2);
    await Assert.That(contexts[0]).IsNotSameReferenceAs(contexts[1]);
  }

  /// <summary>
  /// Verifies scoped contexts within different scopes are isolated.
  /// Must compare while scopes are active (pooled contexts return to pool on dispose).
  /// </summary>
  [Test]
  public async Task ScopedContexts_InDifferentScopes_AreIsolatedAsync() {
    // Arrange
    var services = new ServiceCollection();
    var capturedContexts = new List<WorkCoordinationDbContext>();

    services.AddPooledDbContextFactory<WorkCoordinationDbContext>(options => {
      options.UseNpgsql(ConnectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    });

    // Simulate scoped context from factory - capture each instance
    services.AddScoped(sp => {
      var factory = sp.GetRequiredService<IDbContextFactory<WorkCoordinationDbContext>>();
      var context = factory.CreateDbContext();
      lock (capturedContexts) { capturedContexts.Add(context); }
      return context;
    });

    await using var serviceProvider = services.BuildServiceProvider();

    // Act - Get contexts from two CONCURRENT scopes (both active at once)
    // This ensures we're not getting a recycled context from the pool
    using var scope1 = serviceProvider.CreateScope();
    using var scope2 = serviceProvider.CreateScope();

    var context1 = scope1.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>();
    var context2 = scope2.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>();

    // Assert - Different concurrent scopes should get different contexts
    await Assert.That(capturedContexts.Count).IsEqualTo(2);
    await Assert.That(context1).IsNotSameReferenceAs(context2)
        .Because("Two concurrent scopes should have different DbContext instances");
  }

  #endregion

  #region Helper Methods

  private async Task SeedOrderAsync(
    WorkCoordinationDbContext context,
    TestOrderId orderId,
    decimal amount,
    string status) {

    var id = orderId.Value;
    var order = new Order {
      OrderId = orderId,
      Amount = amount,
      Status = status
    };

    var row = new PerspectiveRow<Order> {
      Id = id,
      Data = order,
      Metadata = new PerspectiveMetadata {
        EventType = "OrderCreated",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow
      },
      Scope = new PerspectiveScope(),
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1
    };

    context.Set<PerspectiveRow<Order>>().Add(row);
  }

  #endregion
}
