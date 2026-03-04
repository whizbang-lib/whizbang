using Dapper;
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

    var result = await conn.QuerySingleAsync<(int version, string scope)>(
      "SELECT version, scope::text FROM wh_per_order WHERE id = @id",
      new { id = testId });

    await Assert.That(result.version).IsEqualTo(2);
    await Assert.That(result.scope).Contains("tenant-2");
    await Assert.That(result.scope).Contains("user:bob");
    await Assert.That(result.scope).Contains("eu-central");
  }
}
