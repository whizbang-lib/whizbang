using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for in-place update methods in <see cref="BaseUpsertStrategy"/>.
/// These methods are required to avoid EF Core 10's ComplexProperty().ToJson()
/// index corruption when updating tracked entities with collection properties.
/// </summary>
/// <tests>Whizbang.Data.EFCore.Postgres.Tests/BaseUpsertStrategyInPlaceUpdateTests.cs</tests>
[NotInParallel("EFCorePostgresTests")]
public class BaseUpsertStrategyInPlaceUpdateTests : EFCoreTestBase {

  // ============================================================================
  // Test Adapter - Exposes protected methods for unit testing
  // ============================================================================

  /// <summary>
  /// Test adapter that exposes protected methods from BaseUpsertStrategy for unit testing.
  /// </summary>
  private sealed class TestableUpsertStrategy : BaseUpsertStrategy {
    protected override bool ClearChangeTrackerAfterSave => true;

    public static void TestUpdateMetadataInPlace(PerspectiveMetadata target, PerspectiveMetadata source)
      => UpdateMetadataInPlace(target, source);

    public static void TestUpdateScopeInPlace(PerspectiveScope target, PerspectiveScope source)
      => UpdateScopeInPlace(target, source);

    public static PerspectiveMetadata TestCloneMetadata(PerspectiveMetadata metadata)
      => CloneMetadata(metadata);

    public static PerspectiveScope TestCloneScope(PerspectiveScope scope)
      => CloneScope(scope);
  }

  // ============================================================================
  // UpdateMetadataInPlace Tests
  // ============================================================================

  /// <summary>
  /// Verifies all 5 metadata properties are copied from source to target.
  /// </summary>
  [Test]
  public async Task UpdateMetadataInPlace_AllProperties_UpdatesTargetCorrectlyAsync() {
    // Arrange
    var target = new PerspectiveMetadata {
      EventType = "OldType",
      EventId = "old-event-id",
      Timestamp = DateTime.UtcNow.AddDays(-1),
      CorrelationId = "old-correlation",
      CausationId = "old-causation"
    };

    var source = new PerspectiveMetadata {
      EventType = "NewType",
      EventId = "new-event-id",
      Timestamp = DateTime.UtcNow,
      CorrelationId = "new-correlation",
      CausationId = "new-causation"
    };

    // Act
    TestableUpsertStrategy.TestUpdateMetadataInPlace(target, source);

    // Assert
    await Assert.That(target.EventType).IsEqualTo("NewType");
    await Assert.That(target.EventId).IsEqualTo("new-event-id");
    await Assert.That(target.Timestamp).IsEqualTo(source.Timestamp);
    await Assert.That(target.CorrelationId).IsEqualTo("new-correlation");
    await Assert.That(target.CausationId).IsEqualTo("new-causation");
  }

  // ============================================================================
  // UpdateScopeInPlace Tests - Scalar Properties
  // ============================================================================

  /// <summary>
  /// Verifies TenantId, CustomerId, UserId, OrganizationId are updated.
  /// </summary>
  [Test]
  public async Task UpdateScopeInPlace_ScalarProperties_UpdatesTargetCorrectlyAsync() {
    // Arrange
    var target = new PerspectiveScope {
      TenantId = "old-tenant",
      CustomerId = "old-customer",
      UserId = "old-user",
      OrganizationId = "old-org"
    };

    var source = new PerspectiveScope {
      TenantId = "new-tenant",
      CustomerId = "new-customer",
      UserId = "new-user",
      OrganizationId = "new-org"
    };

    // Act
    TestableUpsertStrategy.TestUpdateScopeInPlace(target, source);

    // Assert
    await Assert.That(target.TenantId).IsEqualTo("new-tenant");
    await Assert.That(target.CustomerId).IsEqualTo("new-customer");
    await Assert.That(target.UserId).IsEqualTo("new-user");
    await Assert.That(target.OrganizationId).IsEqualTo("new-org");
  }

  // ============================================================================
  // UpdateScopeInPlace Tests - AllowedPrincipals Collection
  // ============================================================================

  /// <summary>
  /// Verifies target list is cleared and repopulated with source items.
  /// </summary>
  [Test]
  public async Task UpdateScopeInPlace_AllowedPrincipals_ClearsAndAddsNewItemsAsync() {
    // Arrange
    var target = new PerspectiveScope {
      AllowedPrincipals = ["user:alice", "group:admins", "user:bob"]
    };

    var source = new PerspectiveScope {
      AllowedPrincipals = ["user:charlie", "service:api"]
    };

    // Act
    TestableUpsertStrategy.TestUpdateScopeInPlace(target, source);

    // Assert
    await Assert.That(target.AllowedPrincipals.Count).IsEqualTo(2);
    await Assert.That(target.AllowedPrincipals).Contains("user:charlie");
    await Assert.That(target.AllowedPrincipals).Contains("service:api");
    await Assert.That(target.AllowedPrincipals).DoesNotContain("user:alice");
  }

  /// <summary>
  /// Branch coverage: empty source should clear target.
  /// </summary>
  [Test]
  public async Task UpdateScopeInPlace_AllowedPrincipals_EmptySource_ClearsTargetAsync() {
    // Arrange
    var target = new PerspectiveScope {
      AllowedPrincipals = ["user:alice", "group:admins"]
    };

    var source = new PerspectiveScope {
      AllowedPrincipals = []
    };

    // Act
    TestableUpsertStrategy.TestUpdateScopeInPlace(target, source);

    // Assert
    await Assert.That(target.AllowedPrincipals).IsEmpty();
  }

  // ============================================================================
  // UpdateScopeInPlace Tests - Extensions Collection
  // ============================================================================

  /// <summary>
  /// Verifies target extensions list is cleared and repopulated with NEW ScopeExtension instances.
  /// </summary>
  [Test]
  public async Task UpdateScopeInPlace_Extensions_ClearsAndAddsNewItemsAsync() {
    // Arrange
    var target = new PerspectiveScope();
    target.SetExtension("oldKey1", "oldValue1");
    target.SetExtension("oldKey2", "oldValue2");

    var source = new PerspectiveScope();
    source.SetExtension("newKey", "newValue");

    // Act
    TestableUpsertStrategy.TestUpdateScopeInPlace(target, source);

    // Assert
    await Assert.That(target.Extensions.Count).IsEqualTo(1);
    await Assert.That(target.Extensions[0].Key).IsEqualTo("newKey");
    await Assert.That(target.Extensions[0].Value).IsEqualTo("newValue");
  }

  /// <summary>
  /// Branch coverage: empty source extensions should clear target.
  /// </summary>
  [Test]
  public async Task UpdateScopeInPlace_Extensions_EmptySource_ClearsTargetAsync() {
    // Arrange
    var target = new PerspectiveScope();
    target.SetExtension("key1", "value1");
    target.SetExtension("key2", "value2");

    var source = new PerspectiveScope(); // Empty extensions

    // Act
    TestableUpsertStrategy.TestUpdateScopeInPlace(target, source);

    // Assert
    await Assert.That(target.Extensions).IsEmpty();
  }

  // ============================================================================
  // CRITICAL: List Instance Preservation Test
  // ============================================================================

  /// <summary>
  /// CRITICAL: Verifies target.AllowedPrincipals and target.Extensions are the SAME
  /// List instance before and after the update. This is essential to avoid
  /// EF Core's InternalComplexCollectionEntry index corruption.
  /// </summary>
  [Test]
  public async Task UpdateScopeInPlace_PreservesListInstance_DoesNotReplaceReferenceAsync() {
    // Arrange
    var target = new PerspectiveScope {
      AllowedPrincipals = ["user:initial"]
    };
    target.SetExtension("initialKey", "initialValue");

    // Capture list references BEFORE update
    var principalsListBefore = target.AllowedPrincipals;
    var extensionsListBefore = target.Extensions;

    var source = new PerspectiveScope {
      AllowedPrincipals = ["user:updated"]
    };
    source.SetExtension("updatedKey", "updatedValue");

    // Act
    TestableUpsertStrategy.TestUpdateScopeInPlace(target, source);

    // Assert - Lists should be the SAME instance (ReferenceEquals)
    await Assert.That(ReferenceEquals(target.AllowedPrincipals, principalsListBefore)).IsTrue()
      .Because("AllowedPrincipals list instance must be preserved for EF Core tracking");
    await Assert.That(ReferenceEquals(target.Extensions, extensionsListBefore)).IsTrue()
      .Because("Extensions list instance must be preserved for EF Core tracking");

    // Also verify the content was updated
    await Assert.That(target.AllowedPrincipals).Contains("user:updated");
    await Assert.That(target.Extensions[0].Key).IsEqualTo("updatedKey");
  }

  // ============================================================================
  // Integration Test: The Actual Bug Fix
  // ============================================================================

  /// <summary>
  /// Integration test that verifies the actual bug is fixed.
  /// This test updates an existing row with complex collections and verifies
  /// SaveChangesAsync does NOT throw ArgumentOutOfRangeException.
  /// </summary>
  [Test]
  public async Task Upsert_ExistingRow_WithComplexCollections_DoesNotThrowOnSaveChangesAsync() {
    // Arrange
    var testId = Guid.CreateVersion7();
    var strategy = new PostgresUpsertStrategy();

    var initialScope = new PerspectiveScope {
      TenantId = "tenant-1",
      AllowedPrincipals = ["user:alice", "group:admins"]
    };
    initialScope.SetExtension("region", "us-west");

    var metadata = new PerspectiveMetadata {
      EventType = "OrderCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };

    var initialOrder = new Order {
      OrderId = new TestOrderId(testId),
      Amount = 100.00m,
      Status = "Created"
    };

    // Create initial record with complex collections
    await using (var context = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
        context,
        "wh_per_order",
        testId,
        initialOrder,
        metadata,
        initialScope);
    }

    // Act - Update the same row with DIFFERENT complex collection values
    // This is where the bug occurred: replacing PerspectiveScope corrupted EF Core tracking
    var updatedScope = new PerspectiveScope {
      TenantId = "tenant-2",
      AllowedPrincipals = ["user:bob", "service:api"] // Different principals
    };
    updatedScope.SetExtension("region", "eu-central"); // Different extension
    updatedScope.SetExtension("priority", "high"); // Additional extension

    var updatedMetadata = new PerspectiveMetadata {
      EventType = "OrderUpdated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow,
      CorrelationId = "corr-123"
    };

    var updatedOrder = new Order {
      OrderId = new TestOrderId(testId),
      Amount = 200.00m,
      Status = "Updated"
    };

    // This should NOT throw ArgumentOutOfRangeException
    await using (var context = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
        context,
        "wh_per_order",
        testId,
        updatedOrder,
        updatedMetadata,
        updatedScope);
    }

    // Assert - Verify data was persisted correctly
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var (version, scope) = await conn.QuerySingleAsync<(int version, string scope)>(
      "SELECT version, scope::text FROM wh_per_order WHERE id = @id",
      new { id = testId });

    await Assert.That(version).IsEqualTo(2);
    // SECURITY: Scope is set only on INSERT and preserved on UPDATE.
    // The original scope (tenant-1, user:alice, us-west) must be retained.
    await Assert.That(scope).Contains("tenant-1");
    await Assert.That(scope).Contains("user:alice");
    await Assert.That(scope).Contains("us-west");
  }

  // ============================================================================
  // CloneMetadata Tests
  // ============================================================================

  /// <summary>
  /// Verifies CloneMetadata creates a new instance with all properties copied.
  /// </summary>
  [Test]
  public async Task CloneMetadata_AllProperties_CreatesNewInstanceWithCopiedValuesAsync() {
    // Arrange
    var original = new PerspectiveMetadata {
      EventType = "OrderCreated",
      EventId = "event-123",
      Timestamp = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc),
      CorrelationId = "corr-456",
      CausationId = "cause-789"
    };

    // Act
    var clone = TestableUpsertStrategy.TestCloneMetadata(original);

    // Assert - Values are equal
    await Assert.That(clone.EventType).IsEqualTo("OrderCreated");
    await Assert.That(clone.EventId).IsEqualTo("event-123");
    await Assert.That(clone.Timestamp).IsEqualTo(original.Timestamp);
    await Assert.That(clone.CorrelationId).IsEqualTo("corr-456");
    await Assert.That(clone.CausationId).IsEqualTo("cause-789");

    // Assert - It's a new instance
    await Assert.That(ReferenceEquals(clone, original)).IsFalse()
      .Because("Clone should be a new instance, not the same reference");
  }

  /// <summary>
  /// Verifies CloneMetadata handles null optional properties.
  /// </summary>
  [Test]
  public async Task CloneMetadata_WithNullOptionalProperties_ClonesCorrectlyAsync() {
    // Arrange
    var original = new PerspectiveMetadata {
      EventType = "TestEvent",
      EventId = "event-id",
      Timestamp = DateTime.UtcNow,
      CorrelationId = null,
      CausationId = null
    };

    // Act
    var clone = TestableUpsertStrategy.TestCloneMetadata(original);

    // Assert
    await Assert.That(clone.EventType).IsEqualTo("TestEvent");
    await Assert.That(clone.CorrelationId).IsNull();
    await Assert.That(clone.CausationId).IsNull();
  }

  // ============================================================================
  // CloneScope Tests
  // ============================================================================

  /// <summary>
  /// Verifies CloneScope creates a new instance with all scalar properties copied.
  /// </summary>
  [Test]
  public async Task CloneScope_ScalarProperties_CreatesNewInstanceWithCopiedValuesAsync() {
    // Arrange
    var original = new PerspectiveScope {
      TenantId = "tenant-abc",
      CustomerId = "customer-123",
      UserId = "user-456",
      OrganizationId = "org-789"
    };

    // Act
    var clone = TestableUpsertStrategy.TestCloneScope(original);

    // Assert - Values are equal
    await Assert.That(clone.TenantId).IsEqualTo("tenant-abc");
    await Assert.That(clone.CustomerId).IsEqualTo("customer-123");
    await Assert.That(clone.UserId).IsEqualTo("user-456");
    await Assert.That(clone.OrganizationId).IsEqualTo("org-789");

    // Assert - It's a new instance
    await Assert.That(ReferenceEquals(clone, original)).IsFalse()
      .Because("Clone should be a new instance, not the same reference");
  }

  /// <summary>
  /// Verifies CloneScope creates new list instances for AllowedPrincipals.
  /// </summary>
  [Test]
  public async Task CloneScope_AllowedPrincipals_CreatesNewListWithCopiedItemsAsync() {
    // Arrange
    var original = new PerspectiveScope {
      AllowedPrincipals = ["user:alice", "group:admins", "service:api"]
    };

    // Act
    var clone = TestableUpsertStrategy.TestCloneScope(original);

    // Assert - Content is equal
    await Assert.That(clone.AllowedPrincipals.Count).IsEqualTo(3);
    await Assert.That(clone.AllowedPrincipals).Contains("user:alice");
    await Assert.That(clone.AllowedPrincipals).Contains("group:admins");
    await Assert.That(clone.AllowedPrincipals).Contains("service:api");

    // Assert - It's a new list instance
    await Assert.That(ReferenceEquals(clone.AllowedPrincipals, original.AllowedPrincipals)).IsFalse()
      .Because("Clone should have a new list instance");
  }

  /// <summary>
  /// Verifies CloneScope creates new ScopeExtension instances in Extensions.
  /// </summary>
  [Test]
  public async Task CloneScope_Extensions_CreatesNewListWithNewExtensionInstancesAsync() {
    // Arrange
    var original = new PerspectiveScope();
    original.SetExtension("region", "us-west");
    original.SetExtension("priority", "high");

    // Act
    var clone = TestableUpsertStrategy.TestCloneScope(original);

    // Assert - Content is equal
    await Assert.That(clone.Extensions.Count).IsEqualTo(2);
    await Assert.That(clone.GetValue("region")).IsEqualTo("us-west");
    await Assert.That(clone.GetValue("priority")).IsEqualTo("high");

    // Assert - It's a new list instance
    await Assert.That(ReferenceEquals(clone.Extensions, original.Extensions)).IsFalse()
      .Because("Clone should have a new Extensions list instance");
  }

  /// <summary>
  /// Verifies CloneScope handles empty collections.
  /// </summary>
  [Test]
  public async Task CloneScope_EmptyCollections_ClonesCorrectlyAsync() {
    // Arrange
    var original = new PerspectiveScope {
      TenantId = "tenant",
      AllowedPrincipals = [],
      // Extensions is empty by default
    };

    // Act
    var clone = TestableUpsertStrategy.TestCloneScope(original);

    // Assert
    await Assert.That(clone.TenantId).IsEqualTo("tenant");
    await Assert.That(clone.AllowedPrincipals).IsEmpty();
    await Assert.That(clone.Extensions).IsEmpty();
  }

  // ============================================================================
  // Local Entity Detachment Tests
  // ============================================================================

  /// <summary>
  /// Tests that when an entity is already tracked locally, it is detached before upsert.
  /// This covers the if (localRow != null) branch in _upsertCoreAsync.
  /// </summary>
  [Test]
  public async Task Upsert_WhenEntityAlreadyTrackedLocally_DetachesAndUpsertsSuccessfullyAsync() {
    // Arrange
    var testId = Guid.CreateVersion7();
    var strategy = new PostgresUpsertStrategy();

    var metadata = new PerspectiveMetadata {
      EventType = "OrderCreated",
      EventId = Guid.NewGuid().ToString(),
      Timestamp = DateTime.UtcNow
    };
    var scope = new PerspectiveScope { TenantId = "tenant-1" };

    var initialOrder = new Order {
      OrderId = new TestOrderId(testId),
      Amount = 100.00m,
      Status = "Created"
    };

    // Create the initial record
    await using (var context = CreateDbContext()) {
      await strategy.UpsertPerspectiveRowAsync(
        context,
        "wh_per_order",
        testId,
        initialOrder,
        metadata,
        scope);
    }

    // Act - Use a context where the entity is already tracked locally
    await using (var context = CreateDbContext()) {
      // First, load the entity into the local tracker (simulating prior operations)
      var existingRow = await context.Set<PerspectiveRow<Order>>()
        .FirstAsync(r => r.Id == testId);

      // Verify it's tracked
      var trackedEntities = context.ChangeTracker.Entries().Count();
      await Assert.That(trackedEntities).IsGreaterThan(0)
        .Because("Entity should be tracked before upsert");

      // Now upsert with updated data - this should detach the tracked entity
      var updatedOrder = new Order {
        OrderId = new TestOrderId(testId),
        Amount = 200.00m,
        Status = "Updated"
      };

      var updatedMetadata = new PerspectiveMetadata {
        EventType = "OrderUpdated",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow
      };

      // This should NOT throw despite the entity being tracked
      await strategy.UpsertPerspectiveRowAsync(
        context,
        "wh_per_order",
        testId,
        updatedOrder,
        updatedMetadata,
        scope);
    }

    // Assert - Verify the update was persisted
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    var (version, amount, status) = await conn.QuerySingleAsync<(int version, decimal amount, string status)>(
      "SELECT version, (data->>'Amount')::decimal as amount, data->>'Status' as status FROM wh_per_order WHERE id = @id",
      new { id = testId });

    await Assert.That(version).IsEqualTo(2);
    await Assert.That(amount).IsEqualTo(200.00m);
    await Assert.That(status).IsEqualTo("Updated");
  }
}
