using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for EFCoreFilterableLensQuery with scope filtering.
/// Tests all filter combinations: tenant, user, organization, customer, principal.
/// </summary>
[Category("Integration")]
public class EFCoreFilterableLensQueryTests : EFCoreTestBase {
  private readonly Uuid7IdProvider _idProvider = new Uuid7IdProvider();

  // === Helper Methods ===

  private async Task _seedOrderAsync(
      DbContext context,
      Guid orderId,
      decimal amount,
      string? tenantId = null,
      string? userId = null,
      string? organizationId = null,
      string? customerId = null,
      IReadOnlyList<SecurityPrincipalId>? allowedPrincipals = null) {

    var order = new Order {
      OrderId = TestOrderId.From(orderId),
      Amount = amount,
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
        OrganizationId = organizationId,
        CustomerId = customerId,
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

  // === Tests for No Filtering ===

  [Test]
  public async Task Query_NoFilter_ReturnsAllRowsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    await _seedOrderAsync(context, order1Id, 100m, tenantId: "tenant-1");
    await _seedOrderAsync(context, order2Id, 200m, tenantId: "tenant-2");

    // Apply empty filter (no filtering)
    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.None,
      SecurityPrincipals = new HashSet<SecurityPrincipalId>()
    });

    // Act
    var result = await lensQuery.Query.ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(2);
  }

  // === Tests for Tenant Filtering ===

  [Test]
  public async Task Query_TenantFilter_ReturnsOnlyTenantRowsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    var order3Id = _idProvider.NewGuid();
    await _seedOrderAsync(context, order1Id, 100m, tenantId: "tenant-1");
    await _seedOrderAsync(context, order2Id, 200m, tenantId: "tenant-1");
    await _seedOrderAsync(context, order3Id, 300m, tenantId: "tenant-2");

    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant,
      TenantId = "tenant-1",
      SecurityPrincipals = new HashSet<SecurityPrincipalId>()
    });

    // Act
    var result = await lensQuery.Query.ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(2);
    await Assert.That(result.All(r => r.Scope.TenantId == "tenant-1")).IsTrue();
  }

  // === Tests for User Filtering ===

  [Test]
  public async Task Query_TenantAndUserFilter_ReturnsOnlyUserRowsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    var order3Id = _idProvider.NewGuid();
    await _seedOrderAsync(context, order1Id, 100m, tenantId: "tenant-1", userId: "user-alice");
    await _seedOrderAsync(context, order2Id, 200m, tenantId: "tenant-1", userId: "user-bob");
    await _seedOrderAsync(context, order3Id, 300m, tenantId: "tenant-2", userId: "user-alice");

    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant | ScopeFilter.User,
      TenantId = "tenant-1",
      UserId = "user-alice",
      SecurityPrincipals = new HashSet<SecurityPrincipalId>()
    });

    // Act
    var result = await lensQuery.Query.ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(order1Id);
  }

  // === Tests for Organization Filtering ===

  [Test]
  public async Task Query_TenantAndOrganizationFilter_ReturnsOnlyOrgRowsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    await _seedOrderAsync(context, order1Id, 100m, tenantId: "tenant-1", organizationId: "org-sales");
    await _seedOrderAsync(context, order2Id, 200m, tenantId: "tenant-1", organizationId: "org-engineering");

    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant | ScopeFilter.Organization,
      TenantId = "tenant-1",
      OrganizationId = "org-sales",
      SecurityPrincipals = new HashSet<SecurityPrincipalId>()
    });

    // Act
    var result = await lensQuery.Query.ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(order1Id);
  }

  // === Tests for Customer Filtering ===

  [Test]
  public async Task Query_TenantAndCustomerFilter_ReturnsOnlyCustomerRowsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    await _seedOrderAsync(context, order1Id, 100m, tenantId: "tenant-1", customerId: "customer-acme");
    await _seedOrderAsync(context, order2Id, 200m, tenantId: "tenant-1", customerId: "customer-globex");

    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant | ScopeFilter.Customer,
      TenantId = "tenant-1",
      CustomerId = "customer-acme",
      SecurityPrincipals = new HashSet<SecurityPrincipalId>()
    });

    // Act
    var result = await lensQuery.Query.ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(order1Id);
  }

  // === Tests for Principal Filtering ===

  [Test]
  public async Task Query_TenantAndPrincipalFilter_ReturnsMatchingPrincipalRowsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    var order3Id = _idProvider.NewGuid();

    // Order 1: Shared with sales-team
    await _seedOrderAsync(context, order1Id, 100m,
      tenantId: "tenant-1",
      allowedPrincipals: new List<SecurityPrincipalId> { SecurityPrincipalId.Group("sales-team") });

    // Order 2: Shared with engineering-team
    await _seedOrderAsync(context, order2Id, 200m,
      tenantId: "tenant-1",
      allowedPrincipals: new List<SecurityPrincipalId> { SecurityPrincipalId.Group("engineering-team") });

    // Order 3: Shared with both teams
    await _seedOrderAsync(context, order3Id, 300m,
      tenantId: "tenant-1",
      allowedPrincipals: new List<SecurityPrincipalId> {
        SecurityPrincipalId.Group("sales-team"),
        SecurityPrincipalId.Group("managers")
      });

    var callerPrincipals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.Group("sales-team")
    };

    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant | ScopeFilter.Principal,
      TenantId = "tenant-1",
      SecurityPrincipals = callerPrincipals,
      UseOrLogicForUserAndPrincipal = false
    });

    // Act
    var result = await lensQuery.Query.ToListAsync();

    // Assert - Should return orders 1 and 3 (both contain sales-team)
    await Assert.That(result.Count).IsEqualTo(2);
    var resultIds = result.Select(r => r.Id).ToHashSet();
    await Assert.That(resultIds.Contains(order1Id)).IsTrue();
    await Assert.That(resultIds.Contains(order3Id)).IsTrue();
    await Assert.That(resultIds.Contains(order2Id)).IsFalse();
  }

  [Test]
  public async Task Query_PrincipalFilter_NoMatch_ReturnsEmptyAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    var orderId = _idProvider.NewGuid();
    await _seedOrderAsync(context, orderId, 100m,
      tenantId: "tenant-1",
      allowedPrincipals: new List<SecurityPrincipalId> { SecurityPrincipalId.Group("sales-team") });

    var callerPrincipals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.Group("engineering-team")
    };

    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant | ScopeFilter.Principal,
      TenantId = "tenant-1",
      SecurityPrincipals = callerPrincipals,
      UseOrLogicForUserAndPrincipal = false
    });

    // Act
    var result = await lensQuery.Query.ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(0);
  }

  // === Tests for User OR Principal (My Records or Shared) ===

  [Test]
  public async Task Query_UserOrPrincipal_ReturnsOwnedAndSharedRowsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    var order3Id = _idProvider.NewGuid();

    // Order 1: Owned by alice (no principals needed)
    await _seedOrderAsync(context, order1Id, 100m,
      tenantId: "tenant-1",
      userId: "user-alice");

    // Order 2: Owned by bob, shared with sales-team (alice is in sales-team)
    await _seedOrderAsync(context, order2Id, 200m,
      tenantId: "tenant-1",
      userId: "user-bob",
      allowedPrincipals: new List<SecurityPrincipalId> { SecurityPrincipalId.Group("sales-team") });

    // Order 3: Owned by charlie, not shared with alice
    await _seedOrderAsync(context, order3Id, 300m,
      tenantId: "tenant-1",
      userId: "user-charlie",
      allowedPrincipals: new List<SecurityPrincipalId> { SecurityPrincipalId.Group("engineering-team") });

    var callerPrincipals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.Group("sales-team")
    };

    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant | ScopeFilter.User | ScopeFilter.Principal,
      TenantId = "tenant-1",
      UserId = "user-alice",
      SecurityPrincipals = callerPrincipals,
      UseOrLogicForUserAndPrincipal = true  // "My records OR shared with me"
    });

    // Act
    var result = await lensQuery.Query.ToListAsync();

    // Assert - Should return orders 1 (owned) and 2 (shared)
    await Assert.That(result.Count).IsEqualTo(2);
    var resultIds = result.Select(r => r.Id).ToHashSet();
    await Assert.That(resultIds.Contains(order1Id)).IsTrue();  // Owned by alice
    await Assert.That(resultIds.Contains(order2Id)).IsTrue();  // Shared with sales-team
    await Assert.That(resultIds.Contains(order3Id)).IsFalse(); // Neither owned nor shared
  }

  [Test]
  public async Task Query_UserOrPrincipal_OwnedButNotShared_ReturnsOwnedAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    var orderId = _idProvider.NewGuid();
    // Order owned by alice, no principals (not shared)
    await _seedOrderAsync(context, orderId, 100m,
      tenantId: "tenant-1",
      userId: "user-alice");

    var callerPrincipals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.Group("some-group")
    };

    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant | ScopeFilter.User | ScopeFilter.Principal,
      TenantId = "tenant-1",
      UserId = "user-alice",
      SecurityPrincipals = callerPrincipals,
      UseOrLogicForUserAndPrincipal = true
    });

    // Act
    var result = await lensQuery.Query.ToListAsync();

    // Assert - Should return the owned order
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(orderId);
  }

  [Test]
  public async Task Query_UserOrPrincipal_SharedButNotOwned_ReturnsSharedAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    var orderId = _idProvider.NewGuid();
    // Order owned by bob, shared with sales-team
    await _seedOrderAsync(context, orderId, 100m,
      tenantId: "tenant-1",
      userId: "user-bob",
      allowedPrincipals: new List<SecurityPrincipalId> { SecurityPrincipalId.Group("sales-team") });

    var callerPrincipals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.User("alice"),
      SecurityPrincipalId.Group("sales-team")
    };

    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant | ScopeFilter.User | ScopeFilter.Principal,
      TenantId = "tenant-1",
      UserId = "user-alice",
      SecurityPrincipals = callerPrincipals,
      UseOrLogicForUserAndPrincipal = true
    });

    // Act
    var result = await lensQuery.Query.ToListAsync();

    // Assert - Should return the shared order
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(orderId);
  }

  // === Tests for GetByIdAsync with Filtering ===

  [Test]
  public async Task GetByIdAsync_WithTenantFilter_ReturnsMatchingRowAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    var orderId = _idProvider.NewGuid();
    await _seedOrderAsync(context, orderId, 100m, tenantId: "tenant-1");

    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant,
      TenantId = "tenant-1",
      SecurityPrincipals = new HashSet<SecurityPrincipalId>()
    });

    // Act
    var result = await lensQuery.GetByIdAsync(orderId);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Amount).IsEqualTo(100m);
  }

  [Test]
  public async Task GetByIdAsync_WithTenantFilter_WrongTenant_ReturnsNullAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    var orderId = _idProvider.NewGuid();
    await _seedOrderAsync(context, orderId, 100m, tenantId: "tenant-1");

    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant,
      TenantId = "tenant-2",  // Wrong tenant
      SecurityPrincipals = new HashSet<SecurityPrincipalId>()
    });

    // Act
    var result = await lensQuery.GetByIdAsync(orderId);

    // Assert - Should not find it because of tenant filter
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetByIdAsync_WithPrincipalFilter_MatchingPrincipal_ReturnsRowAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    var orderId = _idProvider.NewGuid();
    await _seedOrderAsync(context, orderId, 100m,
      tenantId: "tenant-1",
      allowedPrincipals: new List<SecurityPrincipalId> { SecurityPrincipalId.Group("sales-team") });

    var callerPrincipals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.Group("sales-team")
    };

    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant | ScopeFilter.Principal,
      TenantId = "tenant-1",
      SecurityPrincipals = callerPrincipals,
      UseOrLogicForUserAndPrincipal = false
    });

    // Act
    var result = await lensQuery.GetByIdAsync(orderId);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Amount).IsEqualTo(100m);
  }

  [Test]
  public async Task GetByIdAsync_WithPrincipalFilter_NoMatchingPrincipal_ReturnsNullAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    var orderId = _idProvider.NewGuid();
    await _seedOrderAsync(context, orderId, 100m,
      tenantId: "tenant-1",
      allowedPrincipals: new List<SecurityPrincipalId> { SecurityPrincipalId.Group("sales-team") });

    var callerPrincipals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.Group("engineering-team")  // No overlap
    };

    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant | ScopeFilter.Principal,
      TenantId = "tenant-1",
      SecurityPrincipals = callerPrincipals,
      UseOrLogicForUserAndPrincipal = false
    });

    // Act
    var result = await lensQuery.GetByIdAsync(orderId);

    // Assert
    await Assert.That(result).IsNull();
  }

  // === Tests for Combined Multi-Filter Scenarios ===

  [Test]
  public async Task Query_AllFilters_CombinesCorrectlyAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();
    var order3Id = _idProvider.NewGuid();

    // Order 1: Matches all criteria
    await _seedOrderAsync(context, order1Id, 100m,
      tenantId: "tenant-1",
      organizationId: "org-sales",
      customerId: "customer-acme");

    // Order 2: Wrong organization
    await _seedOrderAsync(context, order2Id, 200m,
      tenantId: "tenant-1",
      organizationId: "org-engineering",
      customerId: "customer-acme");

    // Order 3: Wrong tenant
    await _seedOrderAsync(context, order3Id, 300m,
      tenantId: "tenant-2",
      organizationId: "org-sales",
      customerId: "customer-acme");

    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant | ScopeFilter.Organization | ScopeFilter.Customer,
      TenantId = "tenant-1",
      OrganizationId = "org-sales",
      CustomerId = "customer-acme",
      SecurityPrincipals = new HashSet<SecurityPrincipalId>()
    });

    // Act
    var result = await lensQuery.Query.ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(order1Id);
  }

  [Test]
  public async Task Query_TenantPlusPrincipal_EnforcesTenancisolationAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    var order1Id = _idProvider.NewGuid();
    var order2Id = _idProvider.NewGuid();

    // Order 1: Right tenant, matching principal
    await _seedOrderAsync(context, order1Id, 100m,
      tenantId: "tenant-1",
      allowedPrincipals: new List<SecurityPrincipalId> { SecurityPrincipalId.Group("sales-team") });

    // Order 2: Wrong tenant, matching principal (should NOT be returned!)
    await _seedOrderAsync(context, order2Id, 200m,
      tenantId: "tenant-2",
      allowedPrincipals: new List<SecurityPrincipalId> { SecurityPrincipalId.Group("sales-team") });

    var callerPrincipals = new HashSet<SecurityPrincipalId> {
      SecurityPrincipalId.Group("sales-team")
    };

    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant | ScopeFilter.Principal,
      TenantId = "tenant-1",
      SecurityPrincipals = callerPrincipals,
      UseOrLogicForUserAndPrincipal = false
    });

    // Act
    var result = await lensQuery.Query.ToListAsync();

    // Assert - Only order from tenant-1 should be returned
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(order1Id);
  }

  // === Performance/Scale Tests ===

  [Test]
  public async Task Query_With100Principals_HandlesLargePrincipalSetAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    // Create 100 principals for the caller (simulating user with many group memberships)
    var callerPrincipals = new HashSet<SecurityPrincipalId>();
    callerPrincipals.Add(SecurityPrincipalId.User("alice"));
    for (int i = 1; i <= 99; i++) {
      callerPrincipals.Add(SecurityPrincipalId.Group($"group-{i:D3}"));
    }

    // Seed orders with different principals
    var matchingOrderId = _idProvider.NewGuid();
    var nonMatchingOrderId = _idProvider.NewGuid();
    var multiMatchOrderId = _idProvider.NewGuid();

    // Order that matches one of the 100 principals (group-050)
    await _seedOrderAsync(context, matchingOrderId, 100m,
      tenantId: "tenant-1",
      allowedPrincipals: new List<SecurityPrincipalId> {
        SecurityPrincipalId.Group("group-050")
      });

    // Order that doesn't match any principal
    await _seedOrderAsync(context, nonMatchingOrderId, 200m,
      tenantId: "tenant-1",
      allowedPrincipals: new List<SecurityPrincipalId> {
        SecurityPrincipalId.Group("other-group")
      });

    // Order that matches multiple of the caller's principals
    await _seedOrderAsync(context, multiMatchOrderId, 300m,
      tenantId: "tenant-1",
      allowedPrincipals: new List<SecurityPrincipalId> {
        SecurityPrincipalId.User("alice"),
        SecurityPrincipalId.Group("group-001"),
        SecurityPrincipalId.Group("group-099")
      });

    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant | ScopeFilter.Principal,
      TenantId = "tenant-1",
      SecurityPrincipals = callerPrincipals,
      UseOrLogicForUserAndPrincipal = false
    });

    // Act
    var result = await lensQuery.Query.ToListAsync();

    // Assert - Should return both matching orders, not the non-matching one
    await Assert.That(result.Count).IsEqualTo(2);
    var resultIds = result.Select(r => r.Id).ToHashSet();
    await Assert.That(resultIds.Contains(matchingOrderId)).IsTrue();
    await Assert.That(resultIds.Contains(multiMatchOrderId)).IsTrue();
    await Assert.That(resultIds.Contains(nonMatchingOrderId)).IsFalse();
  }

  [Test]
  public async Task Query_With100Principals_CanLogGeneratedSqlAsync() {
    // Arrange - This test verifies the query executes and can be observed for SQL analysis
    await using var context = CreateDbContext();
    var lensQuery = new EFCoreFilterableLensQuery<Order>(context, "wh_per_order");

    // Create 100 principals
    var callerPrincipals = new HashSet<SecurityPrincipalId>();
    for (int i = 0; i < 100; i++) {
      callerPrincipals.Add(SecurityPrincipalId.Group($"group-{i:D3}"));
    }

    // Seed a single order
    var orderId = _idProvider.NewGuid();
    await _seedOrderAsync(context, orderId, 100m,
      tenantId: "tenant-1",
      allowedPrincipals: new List<SecurityPrincipalId> {
        SecurityPrincipalId.Group("group-050")
      });

    lensQuery.ApplyFilter(new ScopeFilterInfo {
      Filters = ScopeFilter.Tenant | ScopeFilter.Principal,
      TenantId = "tenant-1",
      SecurityPrincipals = callerPrincipals,
      UseOrLogicForUserAndPrincipal = false
    });

    // Act - Get the query string for analysis (EF Core's ToQueryString)
    var query = lensQuery.Query;
    var queryString = query.ToQueryString();

    // Log the query for manual inspection (will show in test output)
    Console.WriteLine("=== Generated SQL with 100 principals ===");
    Console.WriteLine(queryString);
    Console.WriteLine($"=== Query string length: {queryString.Length} characters ===");

    // Execute the query
    var result = await query.ToListAsync();

    // Assert
    await Assert.That(result.Count).IsEqualTo(1);
    await Assert.That(result[0].Id).IsEqualTo(orderId);

    // Verify the query contains the expected pattern
    await Assert.That(queryString).Contains("scope");
    await Assert.That(queryString).Contains("TenantId");
  }

  // === Constructor Validation ===

  [Test]
  public async Task Constructor_WithNullContext_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    await Assert.That(() => new EFCoreFilterableLensQuery<Order>(null!, "table"))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullTableName_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    await using var context = CreateDbContext();

    // Act & Assert
    await Assert.That(() => new EFCoreFilterableLensQuery<Order>(context, null!))
      .Throws<ArgumentNullException>();
  }
}
