using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for IScopedLensQuery<T> and ILensQueryFactory<T> with real PostgreSQL.
/// Verifies scope management, DbContext lifecycle, and connection pooling behavior.
/// </summary>
/// <tests>Whizbang.Core.Tests/Lenses/ScopedLensQueryTests.cs</tests>
/// <tests>Whizbang.Core.Tests/Lenses/LensQueryFactoryTests.cs</tests>
[Category("Integration")]
public class ScopedLensQueryIntegrationTests : EFCoreTestBase {
  private readonly Uuid7IdProvider _idProvider = new Uuid7IdProvider();

  private IServiceProvider BuildServiceProvider() {
    var services = new ServiceCollection();

    // Register DbContext as scoped (standard EF Core pattern)
    services.AddScoped(_ => CreateDbContext());

    // Register ILensQuery<Order> as scoped (wraps scoped DbContext)
    services.AddScoped<ILensQuery<Order>>(sp => {
      var context = sp.GetRequiredService<WorkCoordinationDbContext>();
      return new EFCorePostgresLensQuery<Order>(context, "orders_perspective");
    });

    // Register IScopedLensQuery<Order> as singleton (auto-creates scope per operation)
    services.AddSingleton<IScopedLensQuery<Order>>(sp => {
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new ScopedLensQuery<Order>(scopeFactory);
    });

    // Register ILensQueryFactory<Order> as singleton (manual scope control)
    services.AddSingleton<ILensQueryFactory<Order>>(sp => {
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new LensQueryFactory<Order>(scopeFactory);
    });

    return services.BuildServiceProvider();
  }

  private async Task<Guid> SeedOrderAsync(
    WorkCoordinationDbContext context,
    TestOrderId orderId,
    decimal amount,
    string status) {

    var id = orderId.Value;  // Use provided orderId as the PerspectiveRow.Id
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
    await context.SaveChangesAsync();

    return id;
  }

  [Test]
  public async Task IScopedLensQuery_GetByIdAsync_FetchesFreshDataAsync() {
    // Arrange
    var serviceProvider = BuildServiceProvider();
    var scopedQuery = serviceProvider.GetRequiredService<IScopedLensQuery<Order>>();

    // Seed initial data
    await using (var seedContext = CreateDbContext()) {
      var orderId = TestOrderId.From(Guid.NewGuid());
      await SeedOrderAsync(seedContext, orderId, 100.00m, "Created");
    }

    // Act - Query through IScopedLensQuery (auto-creates scope)
    var orderId2 = TestOrderId.From(Guid.NewGuid());
    var result = await scopedQuery.GetByIdAsync(orderId2.Value);

    // Assert - Should return null (no order with this ID)
    await Assert.That(result).IsNull();

    // Seed new order with the queried ID
    await using (var seedContext2 = CreateDbContext()) {
      await SeedOrderAsync(seedContext2, orderId2, 200.00m, "Shipped");
    }

    // Act - Query again (should fetch fresh data from new scope)
    var result2 = await scopedQuery.GetByIdAsync(orderId2.Value);

    // Assert - Should return the newly seeded order
    await Assert.That(result2).IsNotNull();
    await Assert.That(result2!.Amount).IsEqualTo(200.00m);
    await Assert.That(result2.Status).IsEqualTo("Shipped");
  }

  [Test]
  public async Task IScopedLensQuery_ExecuteAsync_CreatesAndDisposesScopeAsync() {
    // Arrange
    var serviceProvider = BuildServiceProvider();
    var scopedQuery = serviceProvider.GetRequiredService<IScopedLensQuery<Order>>();

    // Seed test data
    await using (var seedContext = CreateDbContext()) {
      await SeedOrderAsync(seedContext, TestOrderId.From(Guid.NewGuid()), 100.00m, "Created");
      await SeedOrderAsync(seedContext, TestOrderId.From(Guid.NewGuid()), 200.00m, "Shipped");
      await SeedOrderAsync(seedContext, TestOrderId.From(Guid.NewGuid()), 300.00m, "Delivered");
    }

    // Act - Execute query through IScopedLensQuery
    var result = await scopedQuery.ExecuteAsync(async (query, ct) => {
      return await query.Query
        .Where(row => row.Data.Amount >= 200.00m)
        .CountAsync(ct);
    });

    // Assert - Should return count of 2 (orders with amount >= 200)
    await Assert.That(result).IsEqualTo(2);

    // Verify scope was disposed by running another query (should create new scope)
    var result2 = await scopedQuery.ExecuteAsync(async (query, ct) => {
      return await query.Query.CountAsync(ct);
    });

    await Assert.That(result2).IsEqualTo(3);
  }

  [Test]
  public async Task ILensQueryFactory_BatchQueries_ShareSameScopeAsync() {
    // Arrange
    var serviceProvider = BuildServiceProvider();
    var factory = serviceProvider.GetRequiredService<ILensQueryFactory<Order>>();

    // Seed test data
    await using (var seedContext = CreateDbContext()) {
      await SeedOrderAsync(seedContext, TestOrderId.From(Guid.NewGuid()), 100.00m, "Created");
      await SeedOrderAsync(seedContext, TestOrderId.From(Guid.NewGuid()), 200.00m, "Shipped");
      await SeedOrderAsync(seedContext, TestOrderId.From(Guid.NewGuid()), 300.00m, "Delivered");
    }

    // Act - Create scope and run multiple queries within it
    using var scopedQuery = factory.CreateScoped();

    var query1 = await scopedQuery.Value.Query
      .Where(row => row.Data.Status == "Created")
      .CountAsync();

    var query2 = await scopedQuery.Value.Query
      .Where(row => row.Data.Amount >= 200.00m)
      .ToListAsync();

    var query3 = await scopedQuery.Value.Query
      .OrderByDescending(row => row.Data.Amount)
      .Select(row => row.Data)
      .FirstAsync();

    // Assert - All queries should work within the same scope
    await Assert.That(query1).IsEqualTo(1);
    await Assert.That(query2).Count().IsEqualTo(2);
    await Assert.That(query3.Amount).IsEqualTo(300.00m);
  }

  [Test]
  public async Task ILensQueryFactory_MultipleScopes_CreateSeparateDbContextsAsync() {
    // Arrange
    var serviceProvider = BuildServiceProvider();
    var factory = serviceProvider.GetRequiredService<ILensQueryFactory<Order>>();

    // Seed test data
    await using (var seedContext = CreateDbContext()) {
      await SeedOrderAsync(seedContext, TestOrderId.From(Guid.NewGuid()), 100.00m, "Created");
    }

    // Act - Create two separate scopes
    using var scope1 = factory.CreateScoped();
    using var scope2 = factory.CreateScoped();

    var count1 = await scope1.Value.Query.CountAsync();
    var count2 = await scope2.Value.Query.CountAsync();

    // Assert - Both scopes should see the same data (separate DbContexts, same DB)
    await Assert.That(count1).IsEqualTo(1);
    await Assert.That(count2).IsEqualTo(1);

    // Verify they are different instances
    await Assert.That(scope1.Value).IsNotSameReferenceAs(scope2.Value);
  }

  [Test]
  public async Task ScopedQuery_DisposesScope_AndReleasesConnectionAsync() {
    // Arrange
    var serviceProvider = BuildServiceProvider();
    var factory = serviceProvider.GetRequiredService<ILensQueryFactory<Order>>();

    // Seed test data
    await using (var seedContext = CreateDbContext()) {
      await SeedOrderAsync(seedContext, TestOrderId.From(Guid.NewGuid()), 100.00m, "Created");
    }

    // Act - Create scope, use it, and dispose
    LensQueryScope<Order>? scopedQuery = factory.CreateScoped();
    var count = await scopedQuery.Value.Query.CountAsync();
    await Assert.That(count).IsEqualTo(1);

    // Dispose the scope
    scopedQuery.Dispose();
    scopedQuery = null;

    // Assert - Should be able to create new scope after disposal
    using var newScope = factory.CreateScoped();
    var count2 = await newScope.Value.Query.CountAsync();
    await Assert.That(count2).IsEqualTo(1);
  }

  [Test]
  public async Task IScopedLensQuery_QueryAsync_StreamsResultsAsync() {
    // Arrange
    var serviceProvider = BuildServiceProvider();
    var scopedQuery = serviceProvider.GetRequiredService<IScopedLensQuery<Order>>();

    // Seed test data
    await using (var seedContext = CreateDbContext()) {
      await SeedOrderAsync(seedContext, TestOrderId.From(Guid.NewGuid()), 100.00m, "Created");
      await SeedOrderAsync(seedContext, TestOrderId.From(Guid.NewGuid()), 200.00m, "Shipped");
      await SeedOrderAsync(seedContext, TestOrderId.From(Guid.NewGuid()), 300.00m, "Delivered");
    }

    // Act - Stream results through IScopedLensQuery
    var results = new List<PerspectiveRow<Order>>();
    await foreach (var row in scopedQuery.QueryAsync(query =>
      query.Query.Where(row => row.Data.Amount >= 200.00m))) {
      results.Add(row);
    }

    // Assert - Should stream 2 results
    await Assert.That(results).Count().IsEqualTo(2);
    await Assert.That(results.Select(r => r.Data.Amount)).Contains(200.00m);
    await Assert.That(results.Select(r => r.Data.Amount)).Contains(300.00m);
  }
}
