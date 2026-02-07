using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Transports.HotChocolate.Tests.Fixtures;

namespace Whizbang.Transports.HotChocolate.Tests.Integration;

/// <summary>
/// Integration tests for scoped GraphQL queries.
/// Tests that scope middleware properly extracts scope from HTTP context
/// and applies it to lens queries.
/// </summary>
[Category("Integration")]
[Category("GraphQL")]
[Category("Scoping")]
public class ScopedQueryTests {

  [Test]
  public async Task Middleware_WithTenantIdClaim_SetsScopeContextAsync() {
    // Arrange
    await using var server = await ScopedGraphQLTestServer.CreateAsync();

    // Add data for different tenants
    server.OrderLens.AddData([
        TestDataFactory.CreateOrderRow(
            customerName: "TenantA Customer",
            scope: new PerspectiveScope { TenantId = "tenant-a" }),
        TestDataFactory.CreateOrderRow(
            customerName: "TenantB Customer",
            scope: new PerspectiveScope { TenantId = "tenant-b" })
    ]);

    // Act - Query with tenant-a scope
    var result = await server.ExecuteWithScopeAsync(
        """
        {
          orders {
            nodes {
              data {
                customerName
              }
            }
          }
        }
        """,
        new PerspectiveScope { TenantId = "tenant-a" });

    // Assert
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("TenantA Customer");
    await Assert.That(json).DoesNotContain("TenantB Customer");
  }

  [Test]
  public async Task Middleware_WithUserIdClaim_SetsScopeContextAsync() {
    // Arrange
    await using var server = await ScopedGraphQLTestServer.CreateAsync();

    server.OrderLens.AddData([
        TestDataFactory.CreateOrderRow(
            customerName: "User1 Order",
            scope: new PerspectiveScope { UserId = "user-1" }),
        TestDataFactory.CreateOrderRow(
            customerName: "User2 Order",
            scope: new PerspectiveScope { UserId = "user-2" })
    ]);

    // Act - Query with user-1 scope
    var result = await server.ExecuteWithScopeAsync(
        """
        {
          orders {
            nodes {
              data {
                customerName
              }
            }
          }
        }
        """,
        new PerspectiveScope { UserId = "user-1" });

    // Assert
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("User1 Order");
    await Assert.That(json).DoesNotContain("User2 Order");
  }

  [Test]
  public async Task Middleware_WithOrganizationIdClaim_SetsScopeContextAsync() {
    // Arrange
    await using var server = await ScopedGraphQLTestServer.CreateAsync();

    server.OrderLens.AddData([
        TestDataFactory.CreateOrderRow(
            customerName: "Org1 Order",
            scope: new PerspectiveScope { OrganizationId = "org-1" }),
        TestDataFactory.CreateOrderRow(
            customerName: "Org2 Order",
            scope: new PerspectiveScope { OrganizationId = "org-2" })
    ]);

    // Act - Query with org-1 scope
    var result = await server.ExecuteWithScopeAsync(
        """
        {
          orders {
            nodes {
              data {
                customerName
              }
            }
          }
        }
        """,
        new PerspectiveScope { OrganizationId = "org-1" });

    // Assert
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("Org1 Order");
    await Assert.That(json).DoesNotContain("Org2 Order");
  }

  [Test]
  public async Task Middleware_WithMultipleScopeValues_FiltersByAllAsync() {
    // Arrange
    await using var server = await ScopedGraphQLTestServer.CreateAsync();

    server.OrderLens.AddData([
        TestDataFactory.CreateOrderRow(
            customerName: "Match",
            scope: new PerspectiveScope { TenantId = "tenant-a", UserId = "user-1" }),
        TestDataFactory.CreateOrderRow(
            customerName: "Wrong Tenant",
            scope: new PerspectiveScope { TenantId = "tenant-b", UserId = "user-1" }),
        TestDataFactory.CreateOrderRow(
            customerName: "Wrong User",
            scope: new PerspectiveScope { TenantId = "tenant-a", UserId = "user-2" })
    ]);

    // Act - Query with both tenant-a AND user-1
    var result = await server.ExecuteWithScopeAsync(
        """
        {
          orders {
            nodes {
              data {
                customerName
              }
            }
          }
        }
        """,
        new PerspectiveScope { TenantId = "tenant-a", UserId = "user-1" });

    // Assert
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("Match");
    await Assert.That(json).DoesNotContain("Wrong Tenant");
    await Assert.That(json).DoesNotContain("Wrong User");
  }

  [Test]
  public async Task Middleware_WithoutScope_ReturnsAllDataAsync() {
    // Arrange
    await using var server = await ScopedGraphQLTestServer.CreateAsync();

    server.OrderLens.AddData([
        TestDataFactory.CreateOrderRow(customerName: "Order1"),
        TestDataFactory.CreateOrderRow(customerName: "Order2")
    ]);

    // Act - Query without scope (anonymous/admin)
    var result = await server.ExecuteAsync(
        """
        {
          orders {
            nodes {
              data {
                customerName
              }
            }
          }
        }
        """);

    // Assert
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("Order1");
    await Assert.That(json).Contains("Order2");
  }

  [Test]
  public async Task Middleware_WithSecurityPrincipals_FiltersCorrectlyAsync() {
    // Arrange
    await using var server = await ScopedGraphQLTestServer.CreateAsync();

    var salesTeam = SecurityPrincipalId.Group("sales-team");
    var supportTeam = SecurityPrincipalId.Group("support-team");
    var user1 = SecurityPrincipalId.User("user-1");

    server.OrderLens.AddData([
        TestDataFactory.CreateOrderRow(
            customerName: "Sales Order",
            scope: new PerspectiveScope { AllowedPrincipals = [salesTeam] }),
        TestDataFactory.CreateOrderRow(
            customerName: "Support Order",
            scope: new PerspectiveScope { AllowedPrincipals = [supportTeam] }),
        TestDataFactory.CreateOrderRow(
            customerName: "User1 Private Order",
            scope: new PerspectiveScope { AllowedPrincipals = [user1] })
    ]);

    // Act - Query as user-1 who is also in sales-team
    var result = await server.ExecuteWithPrincipalsAsync(
        """
        {
          orders {
            nodes {
              data {
                customerName
              }
            }
          }
        }
        """,
        [user1, salesTeam]);

    // Assert
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("Sales Order");
    await Assert.That(json).Contains("User1 Private Order");
    await Assert.That(json).DoesNotContain("Support Order");
  }

  [Test]
  public async Task Middleware_FromHttpHeaders_ExtractsScopeAsync() {
    // Arrange
    await using var server = await ScopedGraphQLTestServer.CreateAsync();

    server.OrderLens.AddData([
        TestDataFactory.CreateOrderRow(
            customerName: "HeaderTenant Order",
            scope: new PerspectiveScope { TenantId = "header-tenant" }),
        TestDataFactory.CreateOrderRow(
            customerName: "Other Order",
            scope: new PerspectiveScope { TenantId = "other-tenant" })
    ]);

    // Act - Query with scope from headers
    var result = await server.ExecuteWithHeadersAsync(
        """
        {
          orders {
            nodes {
              data {
                customerName
              }
            }
          }
        }
        """,
        new Dictionary<string, string> {
          ["X-Tenant-Id"] = "header-tenant"
        });

    // Assert
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("HeaderTenant Order");
    await Assert.That(json).DoesNotContain("Other Order");
  }

  [Test]
  public async Task ScopeContext_IsAccessibleInResolversAsync() {
    // Arrange
    await using var server = await ScopedGraphQLTestServer.CreateAsync();

    // Act - Query that returns scope info
    var result = await server.ExecuteWithScopeAsync(
        """
        {
          currentScope {
            tenantId
            userId
          }
        }
        """,
        new PerspectiveScope { TenantId = "test-tenant", UserId = "test-user" });

    // Assert
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("test-tenant");
    await Assert.That(json).Contains("test-user");
  }
}
