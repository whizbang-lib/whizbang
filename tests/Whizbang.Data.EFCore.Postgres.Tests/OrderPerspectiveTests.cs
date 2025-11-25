using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests demonstrating the complete IPerspectiveStore pattern.
/// These tests show how perspectives use the store abstraction to maintain read models.
/// </summary>
public class OrderPerspectiveTests {
  private static DbContextOptions<TestDbContext> CreateInMemoryOptions() {
    return new DbContextOptionsBuilder<TestDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;
  }

  [Test]
  public async Task OrderPerspective_Update_WithOrderCreatedEvent_SavesOrderModelAsync() {
    // Arrange
    var options = CreateInMemoryOptions();
    await using var context = new TestDbContext(options);
    var store = new EFCorePostgresPerspectiveStore<Order>(context, "order");
    var perspective = new OrderPerspective(store);

    var @event = new SampleOrderCreatedEvent(
      OrderId: "order-123",
      Amount: 99.99m
    );

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Verify the perspective row was saved
    var saved = await context.Set<PerspectiveRow<Order>>()
      .FirstOrDefaultAsync(r => r.Id == "order-123");

    await Assert.That(saved).IsNotNull();
    await Assert.That(saved!.Id).IsEqualTo("order-123");
    await Assert.That(saved.Data).IsNotNull();
    await Assert.That(saved.Data.OrderId).IsEqualTo("order-123");
    await Assert.That(saved.Data.Amount).IsEqualTo(99.99m);
    await Assert.That(saved.Data.Status).IsEqualTo("Created");
    await Assert.That(saved.Version).IsEqualTo(1);
  }

  [Test]
  public async Task OrderPerspective_Update_StoresDefaultMetadataAsync() {
    // Arrange
    var options = CreateInMemoryOptions();
    await using var context = new TestDbContext(options);
    var store = new EFCorePostgresPerspectiveStore<Order>(context, "order");
    var perspective = new OrderPerspective(store);

    var @event = new SampleOrderCreatedEvent("order-456", 50.00m);

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Verify default metadata was created by the store
    var saved = await context.Set<PerspectiveRow<Order>>()
      .FirstOrDefaultAsync(r => r.Id == "order-456");

    await Assert.That(saved).IsNotNull();
    await Assert.That(saved!.Metadata).IsNotNull();
    await Assert.That(saved.Metadata.EventType).IsEqualTo("Unknown"); // Default value
    await Assert.That(saved.Metadata.EventId).IsNotNull(); // Generated GUID
    await Assert.That(saved.Metadata.Timestamp).IsLessThanOrEqualTo(DateTime.UtcNow);
  }

  [Test]
  public async Task OrderPerspective_Update_StoresDefaultScopeAsync() {
    // Arrange
    var options = CreateInMemoryOptions();
    await using var context = new TestDbContext(options);
    var store = new EFCorePostgresPerspectiveStore<Order>(context, "order");
    var perspective = new OrderPerspective(store);

    var @event = new SampleOrderCreatedEvent("order-789", 75.50m);

    // Act
    await perspective.Update(@event, CancellationToken.None);

    // Assert - Verify default scope was created by the store
    var saved = await context.Set<PerspectiveRow<Order>>()
      .FirstOrDefaultAsync(r => r.Id == "order-789");

    await Assert.That(saved).IsNotNull();
    await Assert.That(saved!.Scope).IsNotNull();
    // Default scope has nullable properties, all initially null
    await Assert.That(saved.Scope.TenantId).IsNull();
    await Assert.That(saved.Scope.CustomerId).IsNull();
  }

  [Test]
  public async Task OrderPerspective_Update_SetsTimestampsAsync() {
    // Arrange
    var options = CreateInMemoryOptions();
    await using var context = new TestDbContext(options);
    var store = new EFCorePostgresPerspectiveStore<Order>(context, "order");
    var perspective = new OrderPerspective(store);

    var before = DateTime.UtcNow;
    var @event = new SampleOrderCreatedEvent("order-abc", 100.00m);

    // Act
    await perspective.Update(@event, CancellationToken.None);
    var after = DateTime.UtcNow;

    // Assert - Verify timestamps are set correctly
    var saved = await context.Set<PerspectiveRow<Order>>()
      .FirstOrDefaultAsync(r => r.Id == "order-abc");

    await Assert.That(saved).IsNotNull();
    await Assert.That(saved!.CreatedAt).IsGreaterThanOrEqualTo(before);
    await Assert.That(saved.CreatedAt).IsLessThanOrEqualTo(after);
    await Assert.That(saved.UpdatedAt).IsGreaterThanOrEqualTo(before);
    await Assert.That(saved.UpdatedAt).IsLessThanOrEqualTo(after);
  }

  [Test]
  public async Task OrderPerspective_Update_MultipleEvents_IncrementsVersionAsync() {
    // Arrange
    var options = CreateInMemoryOptions();
    await using var context = new TestDbContext(options);
    var store = new EFCorePostgresPerspectiveStore<Order>(context, "order");
    var perspective = new OrderPerspective(store);

    var orderId = "order-multi";

    // Act - Update same order twice
    await perspective.Update(new SampleOrderCreatedEvent(orderId, 10.00m), CancellationToken.None);
    await perspective.Update(new SampleOrderCreatedEvent(orderId, 20.00m), CancellationToken.None);

    // Assert - Version should be incremented
    var saved = await context.Set<PerspectiveRow<Order>>()
      .FirstOrDefaultAsync(r => r.Id == orderId);

    await Assert.That(saved).IsNotNull();
    await Assert.That(saved!.Version).IsEqualTo(2);
    await Assert.That(saved.Data.Amount).IsEqualTo(20.00m); // Latest value
  }

  /// <summary>
  /// Test DbContext for EF Core InMemory testing.
  /// Note: Uses owned types instead of generated JSON configuration for InMemory compatibility.
  /// The actual PostgreSQL implementation will use the generated ConfigureWhizbangPerspectives() method.
  /// </summary>
  private class TestDbContext : DbContext {
    public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      // Configure PerspectiveRow<Order> for InMemory testing
      // Use owned types instead of .ComplexProperty() / .ToJson() which InMemory doesn't support
      modelBuilder.Entity<PerspectiveRow<Order>>(entity => {
        entity.HasKey(e => e.Id);

        // Use owned types for InMemory provider
        entity.OwnsOne(e => e.Data, data => {
          data.WithOwner();
        });

        entity.OwnsOne(e => e.Metadata, metadata => {
          metadata.WithOwner();
          metadata.Property(m => m.EventType).IsRequired();
          metadata.Property(m => m.EventId).IsRequired();
          metadata.Property(m => m.Timestamp).IsRequired();
        });

        entity.OwnsOne(e => e.Scope, scope => {
          scope.WithOwner();
        });
      });
    }
  }
}
