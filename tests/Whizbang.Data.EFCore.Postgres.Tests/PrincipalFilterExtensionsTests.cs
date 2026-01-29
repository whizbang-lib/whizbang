using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for PrincipalFilterExtensions.
/// Tests JSONB array filtering using PostgreSQL's @&gt; containment operator.
/// </summary>
/// <remarks>
/// These tests verify:
/// 1. AllowedPrincipals are persisted correctly as JSONB arrays
/// 2. FilterByPrincipals returns correct rows based on principal overlap
/// 3. FilterByUserOrPrincipals implements "my records or shared" pattern correctly
/// </remarks>
[Category("Integration")]
public class PrincipalFilterExtensionsTests : EFCoreTestBase {
  private readonly Uuid7IdProvider _idProvider = new();

  // === Helper Methods ===

  private async Task _seedOrderWithPrincipalsAsync(
      DbContext context,
      Guid orderId,
      string tenantId,
      string? userId,
      IReadOnlyList<SecurityPrincipalId>? allowedPrincipals) {

    var order = new Order {
      OrderId = TestOrderId.From(orderId),
      Amount = 100m,
      Status = "Created"
    };

    var row = new PerspectiveRow<Order> {
      Id = orderId,
      Data = order,
      Metadata = new PerspectiveMetadata {
        EventType = "OrderCreated",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow
      },
      Scope = new PerspectiveScope {
        TenantId = tenantId,
        UserId = userId,
        AllowedPrincipals = allowedPrincipals
      },
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1
    };

    context.Set<PerspectiveRow<Order>>().Add(row);
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();
  }

  // === Tests for FilterByPrincipals ===

  [Test]
  public async Task FilterByPrincipals_EmptyCallerPrincipals_ReturnsNoRowsAsync() {
    // Arrange
    await using var context = CreateDbContext();

    var orderId = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, orderId, "tenant-1", "user-1",
      [SecurityPrincipalId.User("user-1")]);

    // Act - Empty principals means no access
    var result = await context.Set<PerspectiveRow<Order>>()
      .FilterByPrincipals(new HashSet<SecurityPrincipalId>())
      .ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FilterByPrincipals_MatchingUserPrincipal_ReturnsRowAsync() {
    // Arrange
    await using var context = CreateDbContext();

    var orderId = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, orderId, "tenant-1", "user-1",
      [SecurityPrincipalId.User("alice")]);

    var callerPrincipals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.User("alice")
    };

    // Act
    var result = await context.Set<PerspectiveRow<Order>>()
      .FilterByPrincipals(callerPrincipals)
      .ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(orderId);
  }

  [Test]
  public async Task FilterByPrincipals_MatchingGroupPrincipal_ReturnsRowAsync() {
    // Arrange
    await using var context = CreateDbContext();

    var orderId = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, orderId, "tenant-1", "user-1",
      [
        SecurityPrincipalId.Group("sales-team"),
        SecurityPrincipalId.Group("managers")
      ]);

    var callerPrincipals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.User("bob"),
      SecurityPrincipalId.Group("sales-team")  // Should match
    };

    // Act
    var result = await context.Set<PerspectiveRow<Order>>()
      .FilterByPrincipals(callerPrincipals)
      .ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(orderId);
  }

  [Test]
  public async Task FilterByPrincipals_NoMatchingPrincipal_ReturnsNoRowsAsync() {
    // Arrange
    await using var context = CreateDbContext();

    var orderId = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, orderId, "tenant-1", "user-1",
      [
        SecurityPrincipalId.User("alice"),
        SecurityPrincipalId.Group("sales-team")
      ]);

    var callerPrincipals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.User("bob"),
      SecurityPrincipalId.Group("engineering")  // No overlap
    };

    // Act
    var result = await context.Set<PerspectiveRow<Order>>()
      .FilterByPrincipals(callerPrincipals)
      .ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FilterByPrincipals_MultipleRowsWithOverlap_ReturnsMatchingRowsAsync() {
    // Arrange
    await using var context = CreateDbContext();

    // Row 1: Shared with sales-team
    var order1Id = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, order1Id, "tenant-1", "user-1",
      [SecurityPrincipalId.Group("sales-team")]);

    // Row 2: Shared with engineering (no overlap with caller)
    var order2Id = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, order2Id, "tenant-1", "user-2",
      [SecurityPrincipalId.Group("engineering")]);

    // Row 3: Shared with both sales-team and managers
    var order3Id = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, order3Id, "tenant-1", "user-3",
      [
        SecurityPrincipalId.Group("sales-team"),
        SecurityPrincipalId.Group("managers")
      ]);

    var callerPrincipals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.User("bob"),
      SecurityPrincipalId.Group("sales-team")  // Should match rows 1 and 3
    };

    // Act
    var result = await context.Set<PerspectiveRow<Order>>()
      .FilterByPrincipals(callerPrincipals)
      .ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(2);
    var resultIds = result.Select(r => r.Id).ToHashSet();
    await Assert.That(resultIds.Contains(order1Id)).IsTrue();
    await Assert.That(resultIds.Contains(order3Id)).IsTrue();
    await Assert.That(resultIds.Contains(order2Id)).IsFalse();
  }

  [Test]
  public async Task FilterByPrincipals_NullAllowedPrincipals_DoesNotMatchAsync() {
    // Arrange
    await using var context = CreateDbContext();

    var orderId = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, orderId, "tenant-1", "user-1",
      null);  // No allowed principals

    var callerPrincipals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.User("alice")
    };

    // Act
    var result = await context.Set<PerspectiveRow<Order>>()
      .FilterByPrincipals(callerPrincipals)
      .ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(0);
  }

  // === Tests for FilterByUserOrPrincipals ===

  [Test]
  public async Task FilterByUserOrPrincipals_MatchingUserId_ReturnsRowAsync() {
    // Arrange
    await using var context = CreateDbContext();

    var orderId = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, orderId, "tenant-1", "user-alice",
      [SecurityPrincipalId.Group("other-team")]);

    var callerPrincipals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.Group("engineering")  // No overlap with row's AllowedPrincipals
    };

    // Act - User matches directly via UserId
    var result = await context.Set<PerspectiveRow<Order>>()
      .FilterByUserOrPrincipals("user-alice", callerPrincipals)
      .ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(orderId);
  }

  [Test]
  public async Task FilterByUserOrPrincipals_MatchingPrincipal_ReturnsRowAsync() {
    // Arrange
    await using var context = CreateDbContext();

    var orderId = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, orderId, "tenant-1", "user-bob",  // Different user
      [SecurityPrincipalId.Group("sales-team")]);

    var callerPrincipals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.Group("sales-team")  // Matches AllowedPrincipals
    };

    // Act - Principal matches via AllowedPrincipals
    var result = await context.Set<PerspectiveRow<Order>>()
      .FilterByUserOrPrincipals("user-alice", callerPrincipals)
      .ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(orderId);
  }

  [Test]
  public async Task FilterByUserOrPrincipals_BothMatch_ReturnsUniqueRowsAsync() {
    // Arrange
    await using var context = CreateDbContext();

    // Row 1: Owned by alice
    var order1Id = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, order1Id, "tenant-1", "user-alice",
      null);  // No shared principals

    // Row 2: Shared with alice's group
    var order2Id = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, order2Id, "tenant-1", "user-bob",
      [SecurityPrincipalId.Group("alice-team")]);

    // Row 3: Neither owned nor shared with alice
    var order3Id = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, order3Id, "tenant-1", "user-charlie",
      [SecurityPrincipalId.Group("other-team")]);

    var callerPrincipals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.Group("alice-team")
    };

    // Act - Should return rows 1 and 2
    var result = await context.Set<PerspectiveRow<Order>>()
      .FilterByUserOrPrincipals("user-alice", callerPrincipals)
      .ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(2);
    var resultIds = result.Select(r => r.Id).ToHashSet();
    await Assert.That(resultIds.Contains(order1Id)).IsTrue();
    await Assert.That(resultIds.Contains(order2Id)).IsTrue();
    await Assert.That(resultIds.Contains(order3Id)).IsFalse();
  }

  [Test]
  public async Task FilterByUserOrPrincipals_NoMatch_ReturnsNoRowsAsync() {
    // Arrange
    await using var context = CreateDbContext();

    var orderId = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, orderId, "tenant-1", "user-bob",
      [SecurityPrincipalId.Group("bob-team")]);

    var callerPrincipals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.Group("alice-team")
    };

    // Act - Neither user nor principal matches
    var result = await context.Set<PerspectiveRow<Order>>()
      .FilterByUserOrPrincipals("user-alice", callerPrincipals)
      .ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(0);
  }

  // === Tests for Array Overlap Mode (>10 principals) ===

  [Test]
  public async Task FilterByPrincipals_LargePrincipalSet_UsesArrayOverlapAndReturnsMatchingRowsAsync() {
    // Arrange - This test verifies the ?| array overlap path is used for >10 principals
    await using var context = CreateDbContext();

    // Row with a specific group principal
    var orderId = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, orderId, "tenant-1", "user-1",
      [SecurityPrincipalId.Group("target-team")]);

    // Row without the target principal
    var otherOrderId = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, otherOrderId, "tenant-1", "user-2",
      [SecurityPrincipalId.Group("other-team")]);

    // Caller has >10 principals (triggers array overlap mode)
    var callerPrincipals = new HashSet<SecurityPrincipalId>();
    for (int i = 0; i < 15; i++) {
      callerPrincipals.Add(SecurityPrincipalId.Group($"group-{i}"));
    }
    callerPrincipals.Add(SecurityPrincipalId.Group("target-team")); // The one that should match

    // Act - Should use ?| operator internally
    var result = await context.Set<PerspectiveRow<Order>>()
      .FilterByPrincipals(callerPrincipals)
      .ToListAsync();

    // Assert - Should return only the row with target-team principal
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(orderId);
  }

  [Test]
  public async Task FilterByPrincipals_LargePrincipalSet_NoMatch_ReturnsNoRowsAsync() {
    // Arrange
    await using var context = CreateDbContext();

    var orderId = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, orderId, "tenant-1", "user-1",
      [SecurityPrincipalId.Group("specific-team")]);

    // Caller has >10 principals but none overlap
    var callerPrincipals = new HashSet<SecurityPrincipalId>();
    for (int i = 0; i < 15; i++) {
      callerPrincipals.Add(SecurityPrincipalId.Group($"no-match-group-{i}"));
    }

    // Act - Should use ?| operator internally
    var result = await context.Set<PerspectiveRow<Order>>()
      .FilterByPrincipals(callerPrincipals)
      .ToListAsync();

    // Assert - No matching principals, should return empty
    await Assert.That(result.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FilterByPrincipals_LargePrincipalSet_MultipleRowsWithOverlap_ReturnsMatchingRowsAsync() {
    // Arrange
    await using var context = CreateDbContext();

    // Row 1: Shared with target-team (should match)
    var order1Id = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, order1Id, "tenant-1", "user-1",
      [SecurityPrincipalId.Group("target-team")]);

    // Row 2: Shared with other-team (no overlap with caller)
    var order2Id = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, order2Id, "tenant-1", "user-2",
      [SecurityPrincipalId.Group("other-team")]);

    // Row 3: Shared with both target-team and managers (should match)
    var order3Id = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, order3Id, "tenant-1", "user-3",
      [
        SecurityPrincipalId.Group("target-team"),
        SecurityPrincipalId.Group("managers")
      ]);

    // Caller has >10 principals including target-team
    var callerPrincipals = new HashSet<SecurityPrincipalId>();
    for (int i = 0; i < 12; i++) {
      callerPrincipals.Add(SecurityPrincipalId.Group($"group-{i}"));
    }
    callerPrincipals.Add(SecurityPrincipalId.Group("target-team")); // Should match rows 1 and 3

    // Act - Should use ?| operator internally
    var result = await context.Set<PerspectiveRow<Order>>()
      .FilterByPrincipals(callerPrincipals)
      .ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(2);
    var resultIds = result.Select(r => r.Id).ToHashSet();
    await Assert.That(resultIds.Contains(order1Id)).IsTrue();
    await Assert.That(resultIds.Contains(order3Id)).IsTrue();
    await Assert.That(resultIds.Contains(order2Id)).IsFalse();
  }

  [Test]
  public async Task FilterByPrincipals_LargePrincipalSet_NullAllowedPrincipals_DoesNotMatchAsync() {
    // Arrange
    await using var context = CreateDbContext();

    var orderId = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, orderId, "tenant-1", "user-1",
      null);  // No allowed principals

    // Caller has >10 principals
    var callerPrincipals = new HashSet<SecurityPrincipalId>();
    for (int i = 0; i < 15; i++) {
      callerPrincipals.Add(SecurityPrincipalId.Group($"group-{i}"));
    }

    // Act - Should use ?| operator internally
    var result = await context.Set<PerspectiveRow<Order>>()
      .FilterByPrincipals(callerPrincipals)
      .ToListAsync();

    // Assert - Null AllowedPrincipals should not match
    await Assert.That(result.Count).IsEqualTo(0);
  }

  [Test]
  public async Task FilterByPrincipals_LargePrincipalSet_MatchingGroupPrincipal_ReturnsRowAsync() {
    // Arrange
    await using var context = CreateDbContext();

    var orderId = _idProvider.NewGuid();
    await _seedOrderWithPrincipalsAsync(
      context, orderId, "tenant-1", "user-1",
      [
        SecurityPrincipalId.Group("sales-team"),
        SecurityPrincipalId.Group("managers")
      ]);

    // Caller has >10 principals, one of which matches
    var callerPrincipals = new HashSet<SecurityPrincipalId>();
    for (int i = 0; i < 12; i++) {
      callerPrincipals.Add(SecurityPrincipalId.User($"user-{i}"));
    }
    callerPrincipals.Add(SecurityPrincipalId.Group("sales-team"));  // Should match

    // Act - Should use ?| operator internally
    var result = await context.Set<PerspectiveRow<Order>>()
      .FilterByPrincipals(callerPrincipals)
      .ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(orderId);
  }
}
