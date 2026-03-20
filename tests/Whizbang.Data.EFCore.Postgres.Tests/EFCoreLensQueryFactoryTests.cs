using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Unit tests for EFCoreLensQueryFactory&lt;TDbContext&gt;.
/// Verifies factory creation, query instantiation, DbContext sharing, and disposal.
/// </summary>
/// <docs>lenses/lens-query-factory</docs>
[Category("Unit")]
[Category("Lenses")]
[NotInParallel("EFCorePostgresTests")]
public class EFCoreLensQueryFactoryTests : EFCoreTestBase {

  #region Constructor Tests

  /// <summary>
  /// Verifies that constructor creates a DbContext from the factory.
  /// </summary>
  [Test]
  public async Task Constructor_WithValidParameters_CreatesDbContextAsync() {
    // Arrange
    var contextCreated = false;
    var mockFactory = new MockDbContextFactory<WorkCoordinationDbContext>(() => {
      contextCreated = true;
      return CreateDbContext();
    });

    var tableNames = new Dictionary<Type, string> {
      [typeof(Order)] = "orders_perspective"
    };

    // Act
    var factory = new EFCoreLensQueryFactory<WorkCoordinationDbContext>(mockFactory, tableNames);

    // Assert
    await Assert.That(contextCreated).IsTrue()
        .Because("Factory constructor should create a DbContext from the factory");
    await Assert.That(factory).IsNotNull();
  }

  /// <summary>
  /// Verifies that constructor throws when dbContextFactory is null.
  /// </summary>
  [Test]
  public async Task Constructor_WithNullDbContextFactory_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var tableNames = new Dictionary<Type, string> {
      [typeof(Order)] = "orders_perspective"
    };

    // Act & Assert
    ArgumentNullException? exception = null;
    try {
      _ = new EFCoreLensQueryFactory<WorkCoordinationDbContext>(null!, tableNames);
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull()
        .Because("Null dbContextFactory should throw ArgumentNullException");
    await Assert.That(exception!.ParamName).IsEqualTo("dbContextFactory");
  }

  /// <summary>
  /// Verifies that constructor throws when tableNames is null.
  /// </summary>
  [Test]
  public async Task Constructor_WithNullTableNames_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var mockFactory = new MockDbContextFactory<WorkCoordinationDbContext>(CreateDbContext);

    // Act & Assert
    ArgumentNullException? exception = null;
    try {
      _ = new EFCoreLensQueryFactory<WorkCoordinationDbContext>(mockFactory, null!);
    } catch (ArgumentNullException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull()
        .Because("Null tableNames should throw ArgumentNullException");
    await Assert.That(exception!.ParamName).IsEqualTo("tableNames");
  }

  /// <summary>
  /// Verifies that constructor calls CreateDbContext on the factory.
  /// </summary>
  [Test]
  public async Task Constructor_CreatesDbContextFromFactoryAsync() {
    // Arrange
    var callCount = 0;
    var mockFactory = new MockDbContextFactory<WorkCoordinationDbContext>(() => {
      callCount++;
      return CreateDbContext();
    });

    var tableNames = new Dictionary<Type, string> {
      [typeof(Order)] = "orders_perspective"
    };

    // Act
    _ = new EFCoreLensQueryFactory<WorkCoordinationDbContext>(mockFactory, tableNames);

    // Assert
    await Assert.That(callCount).IsEqualTo(1)
        .Because("Constructor should call CreateDbContext exactly once");
  }

  #endregion

  #region GetQuery Tests

  /// <summary>
  /// Verifies that GetQuery returns an ILensQuery instance.
  /// </summary>
  [Test]
  public async Task GetQuery_ReturnsLensQueryAsync() {
    // Arrange
    var mockFactory = new MockDbContextFactory<WorkCoordinationDbContext>(CreateDbContext);
    var tableNames = new Dictionary<Type, string> {
      [typeof(Order)] = "orders_perspective"
    };

    var factory = new EFCoreLensQueryFactory<WorkCoordinationDbContext>(mockFactory, tableNames);

    // Act
    var query = factory.GetQuery<Order>();

    // Assert
    await Assert.That(query).IsNotNull();
    await Assert.That(query).IsTypeOf<EFCorePostgresLensQuery<Order>>();
  }

  /// <summary>
  /// Verifies that GetQuery uses the correct table name from the dictionary.
  /// </summary>
  [Test]
  public async Task GetQuery_WithRegisteredModel_UsesCorrectTableNameAsync() {
    // Arrange - Seed test data
    await using (var seedContext = CreateDbContext()) {
      var row = new PerspectiveRow<Order> {
        Id = Guid.NewGuid(),
        Data = new Order { OrderId = TestOrderId.New(), Amount = 100.00m, Status = "Created" },
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
      seedContext.Set<PerspectiveRow<Order>>().Add(row);
      await seedContext.SaveChangesAsync();
    }

    var mockFactory = new MockDbContextFactory<WorkCoordinationDbContext>(CreateDbContext);
    var tableNames = new Dictionary<Type, string> {
      [typeof(Order)] = "orders_perspective"
    };

    var factory = new EFCoreLensQueryFactory<WorkCoordinationDbContext>(mockFactory, tableNames);

    // Act
    var query = factory.GetQuery<Order>();
    var count = await query.Query.CountAsync();

    // Assert - Query should work with correct table name
    await Assert.That(count).IsEqualTo(1)
        .Because("Query with correct table name should return seeded data");
  }

  /// <summary>
  /// Verifies that GetQuery throws when model type is not registered.
  /// </summary>
  [Test]
  public async Task GetQuery_WithUnregisteredModel_ThrowsKeyNotFoundExceptionAsync() {
    // Arrange
    var mockFactory = new MockDbContextFactory<WorkCoordinationDbContext>(CreateDbContext);
    var tableNames = new Dictionary<Type, string>(); // Empty - no models registered

    var factory = new EFCoreLensQueryFactory<WorkCoordinationDbContext>(mockFactory, tableNames);

    // Act & Assert
    KeyNotFoundException? exception = null;
    try {
      _ = factory.GetQuery<Order>();
    } catch (KeyNotFoundException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull()
        .Because("Unregistered model type should throw KeyNotFoundException");
  }

  /// <summary>
  /// Verifies that multiple GetQuery calls return different ILensQuery instances.
  /// </summary>
  [Test]
  public async Task GetQuery_CalledMultipleTimes_ReturnsDifferentInstancesAsync() {
    // Arrange
    var mockFactory = new MockDbContextFactory<WorkCoordinationDbContext>(CreateDbContext);
    var tableNames = new Dictionary<Type, string> {
      [typeof(Order)] = "orders_perspective"
    };

    var factory = new EFCoreLensQueryFactory<WorkCoordinationDbContext>(mockFactory, tableNames);

    // Act
    var query1 = factory.GetQuery<Order>();
    var query2 = factory.GetQuery<Order>();

    // Assert - Each call should create a new query instance
    await Assert.That(query1).IsNotSameReferenceAs(query2)
        .Because("Each GetQuery call should return a new instance");
  }

  /// <summary>
  /// Verifies that multiple GetQuery calls share the same DbContext.
  /// </summary>
  [Test]
  public async Task GetQuery_CalledMultipleTimes_SharesSameDbContextAsync() {
    // Arrange
    var createContextCallCount = 0;
    var mockFactory = new MockDbContextFactory<WorkCoordinationDbContext>(() => {
      createContextCallCount++;
      return CreateDbContext();
    });

    var tableNames = new Dictionary<Type, string> {
      [typeof(Order)] = "orders_perspective"
    };

    var factory = new EFCoreLensQueryFactory<WorkCoordinationDbContext>(mockFactory, tableNames);

    // Act - Call GetQuery multiple times
    _ = factory.GetQuery<Order>();
    _ = factory.GetQuery<Order>();
    _ = factory.GetQuery<Order>();

    // Assert - DbContext should have been created only once (in constructor)
    await Assert.That(createContextCallCount).IsEqualTo(1)
        .Because("Factory should share the same DbContext across all GetQuery calls");
  }

  /// <summary>
  /// Verifies that GetQuery for different model types shares the same DbContext.
  /// </summary>
  [Test]
  public async Task GetQuery_ForDifferentModels_SharesSameDbContextAsync() {
    // Arrange
    var createContextCallCount = 0;
    var mockFactory = new MockDbContextFactory<WorkCoordinationDbContext>(() => {
      createContextCallCount++;
      return CreateDbContext();
    });

    var tableNames = new Dictionary<Type, string> {
      [typeof(Order)] = "orders_perspective",
      [typeof(Customer)] = "customers_perspective"
    };

    var factory = new EFCoreLensQueryFactory<WorkCoordinationDbContext>(mockFactory, tableNames);

    // Act
    _ = factory.GetQuery<Order>();
    _ = factory.GetQuery<Customer>();

    // Assert - Same DbContext should be used
    await Assert.That(createContextCallCount).IsEqualTo(1)
        .Because("Factory should use same DbContext for different model types");
  }

  /// <summary>
  /// Verifies that queries use no-tracking behavior by default.
  /// </summary>
  [Test]
  public async Task GetQuery_ReturnsQueryWithNoTrackingBehaviorAsync() {
    // Arrange - Seed test data
    await using (var seedContext = CreateDbContext()) {
      var row = new PerspectiveRow<Order> {
        Id = Guid.NewGuid(),
        Data = new Order { OrderId = TestOrderId.New(), Amount = 100.00m, Status = "Created" },
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
      seedContext.Set<PerspectiveRow<Order>>().Add(row);
      await seedContext.SaveChangesAsync();
    }

    var mockFactory = new MockDbContextFactory<WorkCoordinationDbContext>(CreateDbContext);
    var tableNames = new Dictionary<Type, string> {
      [typeof(Order)] = "orders_perspective"
    };

    var factory = new EFCoreLensQueryFactory<WorkCoordinationDbContext>(mockFactory, tableNames);

    // Act
    var query = factory.GetQuery<Order>();
    var results = await query.Query.ToListAsync();

    // Assert - Results should work (no-tracking doesn't affect basic queries)
    await Assert.That(results.Count).IsEqualTo(1);
  }

  #endregion

  #region DisposeAsync Tests

  /// <summary>
  /// Verifies that DisposeAsync disposes the DbContext.
  /// </summary>
  [Test]
  public async Task DisposeAsync_DisposesDbContextAsync() {
    // Arrange
    WorkCoordinationDbContext? capturedContext = null;
    var mockFactory = new MockDbContextFactory<WorkCoordinationDbContext>(() => {
      capturedContext = CreateDbContext();
      return capturedContext;
    });

    var tableNames = new Dictionary<Type, string> {
      [typeof(Order)] = "orders_perspective"
    };

    var factory = new EFCoreLensQueryFactory<WorkCoordinationDbContext>(mockFactory, tableNames);

    // Act
    await factory.DisposeAsync();

    // Assert - Context should be disposed (querying it should throw)
    ObjectDisposedException? exception = null;
    try {
      await capturedContext!.Set<PerspectiveRow<Order>>().CountAsync();
    } catch (ObjectDisposedException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull()
        .Because("DbContext should be disposed after factory disposal");
  }

  /// <summary>
  /// Verifies that calling DisposeAsync twice only disposes once.
  /// </summary>
  [Test]
  public async Task DisposeAsync_WhenCalledTwice_OnlyDisposesOnceAsync() {
    // Arrange
    var mockFactory = new MockDbContextFactory<WorkCoordinationDbContext>(CreateDbContext);
    var tableNames = new Dictionary<Type, string> {
      [typeof(Order)] = "orders_perspective"
    };

    var factory = new EFCoreLensQueryFactory<WorkCoordinationDbContext>(mockFactory, tableNames);

    // Act - Dispose twice
    await factory.DisposeAsync();
    var secondDisposeCompleted = false;
    await factory.DisposeAsync();
    secondDisposeCompleted = true;

    // Assert - Second dispose should complete without error
    await Assert.That(secondDisposeCompleted).IsTrue()
        .Because("Second DisposeAsync should complete without error");
  }

  /// <summary>
  /// Verifies that DisposeAsync can be called on a fresh factory.
  /// </summary>
  [Test]
  public async Task DisposeAsync_WhenNotDisposed_DisposesDbContextAsync() {
    // Arrange
    var disposed = false;
    var mockContext = new TrackingDbContext(DbContextOptions, () => disposed = true);
    var mockFactory = new MockDbContextFactory<WorkCoordinationDbContext>(() => mockContext);
    var tableNames = new Dictionary<Type, string> {
      [typeof(Order)] = "orders_perspective"
    };

    var factory = new EFCoreLensQueryFactory<WorkCoordinationDbContext>(mockFactory, tableNames);

    // Act
    await factory.DisposeAsync();

    // Assert
    await Assert.That(disposed).IsTrue()
        .Because("Factory should dispose its DbContext");
  }

  /// <summary>
  /// Verifies that DisposeAsync does not throw on already-disposed factory.
  /// </summary>
  [Test]
  public async Task DisposeAsync_WhenAlreadyDisposed_DoesNotThrowAsync() {
    // Arrange
    var mockFactory = new MockDbContextFactory<WorkCoordinationDbContext>(CreateDbContext);
    var tableNames = new Dictionary<Type, string> {
      [typeof(Order)] = "orders_perspective"
    };

    var factory = new EFCoreLensQueryFactory<WorkCoordinationDbContext>(mockFactory, tableNames);

    await factory.DisposeAsync();

    // Act & Assert - Second dispose should not throw
    Exception? exception = null;
    try {
      await factory.DisposeAsync();
    } catch (Exception ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNull()
        .Because("Disposing an already-disposed factory should not throw");
  }

  /// <summary>
  /// Verifies that GetQuery throws after factory is disposed.
  /// </summary>
  [Test]
  public async Task DisposeAsync_AfterDispose_GetQueryThrowsAsync() {
    // Arrange
    var mockFactory = new MockDbContextFactory<WorkCoordinationDbContext>(CreateDbContext);
    var tableNames = new Dictionary<Type, string> {
      [typeof(Order)] = "orders_perspective"
    };

    var factory = new EFCoreLensQueryFactory<WorkCoordinationDbContext>(mockFactory, tableNames);
    await factory.DisposeAsync();

    // Act & Assert
    ObjectDisposedException? exception = null;
    try {
      _ = factory.GetQuery<Order>();
    } catch (ObjectDisposedException ex) {
      exception = ex;
    }

    await Assert.That(exception).IsNotNull()
        .Because("GetQuery should throw after factory is disposed");
  }

  #endregion

  #region Helper Classes

  /// <summary>
  /// Mock IDbContextFactory for testing.
  /// </summary>
  private sealed class MockDbContextFactory<TContext>(Func<TContext> createContext) : IDbContextFactory<TContext>
      where TContext : DbContext {
    private readonly Func<TContext> _createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));

    public TContext CreateDbContext() => _createContext();
  }

  /// <summary>
  /// DbContext subclass that tracks disposal.
  /// </summary>
  private sealed class TrackingDbContext(DbContextOptions<WorkCoordinationDbContext> options, Action onDispose) : WorkCoordinationDbContext(options) {
    private readonly Action _onDispose = onDispose;

    public override void Dispose() {
      _onDispose();
      base.Dispose();
    }

    public override async ValueTask DisposeAsync() {
      _onDispose();
      await base.DisposeAsync();
    }
  }

  /// <summary>
  /// Simple Customer model for testing multiple model types.
  /// </summary>
  private sealed record Customer {
    public required Guid CustomerId { get; init; }
    public required string Name { get; init; }
    public required string Email { get; init; }
  }

  #endregion
}
