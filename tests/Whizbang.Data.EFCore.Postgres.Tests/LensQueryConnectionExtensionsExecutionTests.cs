using System.Data;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Execution tests for LensQueryConnectionExtensions against real PostgreSQL.
/// Unlike LensQueryConnectionExtensionsTests (which verifies method shapes via reflection),
/// these tests actually invoke ExecuteSqlAsync, GetConnection, and GetConnectionAsync
/// through both an IDbContextAccessor-capable lens query and a plain lens query.
/// </summary>
[Category("RawSql")]
[Category("Integration")]
[Category("Shard1")]
public class LensQueryConnectionExtensionsExecutionTests : EFCoreTestBase {
  private readonly Uuid7IdProvider _idProvider = new();

  // === Test Doubles ===

  /// <summary>
  /// Lens query that exposes its DbContext via the internal IDbContextAccessor interface,
  /// the same opt-in mechanism a compatible production implementation would use.
  /// Holds a factory delegate (not the context itself) so the double owns no disposable state.
  /// </summary>
  private sealed class DbContextAccessorLensQuery(Func<DbContext> contextProvider) : ILensQuery<Order>, IDbContextAccessor {
    public DbContext DbContext => contextProvider();
    public IScopedLensAccess<Order> Scope(QueryScope scope) => throw new NotSupportedException();
    public IScopedLensAccess<Order> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues) => throw new NotSupportedException();
    public IScopedLensAccess<Order> DefaultScope => throw new NotSupportedException();
    public IQueryable<PerspectiveRow<Order>> Query => throw new NotSupportedException();
    public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  /// <summary>
  /// Lens query that does NOT implement IDbContextAccessor - used to exercise the
  /// InvalidOperationException branches of every extension method.
  /// </summary>
  private sealed class PlainLensQuery : ILensQuery<Order> {
    public IScopedLensAccess<Order> Scope(QueryScope scope) => throw new NotSupportedException();
    public IScopedLensAccess<Order> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues) => throw new NotSupportedException();
    public IScopedLensAccess<Order> DefaultScope => throw new NotSupportedException();
    public IQueryable<PerspectiveRow<Order>> Query => throw new NotSupportedException();
    public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  // === Helper Methods ===

  private async Task<Guid> _seedOrderAsync(WorkCoordinationDbContext context, decimal amount) {
    var id = _idProvider.NewGuid();
    var row = new PerspectiveRow<Order> {
      Id = id,
      Data = new Order {
        OrderId = TestOrderId.From(id),
        Amount = amount,
        Status = "Created"
      },
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
    context.ChangeTracker.Clear();
    return id;
  }

  // === ExecuteSqlAsync ===

  [Test]
  public async Task ExecuteSqlAsync_WithDbContextAccessor_ReturnsTypedResultsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var order1Id = await _seedOrderAsync(context, 100m);
    var order2Id = await _seedOrderAsync(context, 200m);
    var lensQuery = new DbContextAccessorLensQuery(() => context);

    // Act - raw SQL against the verified wh_per_order schema (id uuid column)
    var results = await lensQuery.ExecuteSqlAsync<Order, RawSqlOrderIdRow>(
        $"""SELECT id AS "Id" FROM wh_per_order ORDER BY id""");

    // Assert
    await Assert.That(results).Count().IsEqualTo(2);
    var resultIds = results.Select(r => r.Id).ToHashSet();
    await Assert.That(resultIds.Contains(order1Id)).IsTrue();
    await Assert.That(resultIds.Contains(order2Id)).IsTrue();
  }

  [Test]
  public async Task ExecuteSqlAsync_WithInterpolatedParameter_FiltersBySqlParameterAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var targetId = await _seedOrderAsync(context, 100m);
    await _seedOrderAsync(context, 200m);
    var lensQuery = new DbContextAccessorLensQuery(() => context);

    // Act - interpolated value must become a SQL parameter, not string concatenation
    var results = await lensQuery.ExecuteSqlAsync<Order, RawSqlOrderIdRow>(
        $"""SELECT id AS "Id" FROM wh_per_order WHERE id = {targetId}""");

    // Assert
    await Assert.That(results).Count().IsEqualTo(1);
    await Assert.That(results[0].Id).IsEqualTo(targetId);
  }

  [Test]
  public async Task ExecuteSqlAsync_WithoutDbContextAccessor_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var lensQuery = new PlainLensQuery();

    // Act
    InvalidOperationException? exception = null;
    try {
      await lensQuery.ExecuteSqlAsync<Order, RawSqlOrderIdRow>($"SELECT 1");
    } catch (InvalidOperationException ex) {
      exception = ex;
    }

    // Assert
    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.Message).Contains("Raw SQL execution requires");
    await Assert.That(exception.Message).Contains("EFCorePostgresLensQuery");
  }

  [Test]
  public async Task ExecuteSqlAsync_WithNullLensQuery_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    ILensQuery<Order> lensQuery = null!;

    // Act & Assert
    await Assert.That(async () => await lensQuery.ExecuteSqlAsync<Order, RawSqlOrderIdRow>($"SELECT 1"))
        .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ExecuteSqlAsync_WithNullSql_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new DbContextAccessorLensQuery(() => context);

    // Act & Assert
    await Assert.That(async () => await lensQuery.ExecuteSqlAsync<Order, RawSqlOrderIdRow>(null!))
        .Throws<ArgumentNullException>();
  }

  // === GetConnection ===

  [Test]
  public async Task GetConnection_WithDbContextAccessor_ReturnsUnderlyingConnectionAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new DbContextAccessorLensQuery(() => context);

    // Act
    var connection = lensQuery.GetConnection();

    // Assert - must be the exact connection managed by the DbContext (not a copy)
    await Assert.That(connection).IsSameReferenceAs(context.Database.GetDbConnection());
  }

  [Test]
  public async Task GetConnection_WithoutDbContextAccessor_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var lensQuery = new PlainLensQuery();

    // Act
    InvalidOperationException? exception = null;
    try {
      _ = lensQuery.GetConnection();
    } catch (InvalidOperationException ex) {
      exception = ex;
    }

    // Assert
    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.Message).Contains("Connection access requires");
  }

  [Test]
  public async Task GetConnection_WithNullLensQuery_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    ILensQuery<Order> lensQuery = null!;

    // Act & Assert
    await Assert.That(() => lensQuery.GetConnection()).Throws<ArgumentNullException>();
  }

  // === GetConnectionAsync ===

  [Test]
  public async Task GetConnectionAsync_WhenConnectionClosed_OpensConnectionAsync() {
    // Arrange - a fresh DbContext starts with a closed connection
    await using var context = CreateDbContext();
    var lensQuery = new DbContextAccessorLensQuery(() => context);
    var stateBefore = context.Database.GetDbConnection().State;

    // Act
    var connection = await lensQuery.GetConnectionAsync();

    // Assert
    await Assert.That(stateBefore).IsEqualTo(ConnectionState.Closed);
    await Assert.That(connection.State).IsEqualTo(ConnectionState.Open);
    await Assert.That(connection).IsSameReferenceAs(context.Database.GetDbConnection());
  }

  [Test]
  public async Task GetConnectionAsync_WhenConnectionAlreadyOpen_ReturnsSameConnectionAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new DbContextAccessorLensQuery(() => context);
    var firstConnection = await lensQuery.GetConnectionAsync();

    // Act - second call hits the already-open branch (no re-open)
    var secondConnection = await lensQuery.GetConnectionAsync();

    // Assert
    await Assert.That(secondConnection).IsSameReferenceAs(firstConnection);
    await Assert.That(secondConnection.State).IsEqualTo(ConnectionState.Open);
  }

  [Test]
  public async Task GetConnectionAsync_WithoutDbContextAccessor_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var lensQuery = new PlainLensQuery();

    // Act
    InvalidOperationException? exception = null;
    try {
      _ = await lensQuery.GetConnectionAsync();
    } catch (InvalidOperationException ex) {
      exception = ex;
    }

    // Assert
    await Assert.That(exception).IsNotNull();
    await Assert.That(exception!.Message).Contains("Connection access requires");
  }

  [Test]
  public async Task GetConnectionAsync_WithNullLensQuery_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    ILensQuery<Order> lensQuery = null!;

    // Act & Assert
    await Assert.That(async () => await lensQuery.GetConnectionAsync())
        .Throws<ArgumentNullException>();
  }
}

/// <summary>
/// Unmapped projection type materialized by EF Core's SqlQuery in raw SQL tests.
/// Property name matches the quoted column alias in the SQL.
/// </summary>
public sealed class RawSqlOrderIdRow {
  public Guid Id { get; init; }
}
