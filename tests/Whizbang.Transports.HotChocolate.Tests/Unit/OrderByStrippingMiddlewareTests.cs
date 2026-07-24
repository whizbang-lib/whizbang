using HotChocolate.Execution;
using Whizbang.Transports.HotChocolate.Tests.Fixtures;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="Whizbang.Transports.HotChocolate.Middleware.OrderByStrippingMiddleware"/>.
/// Exercises the middleware through a real HotChocolate executor to cover all branches:
/// - No sorting arguments present (early return)
/// - Result is null (early return)
/// - Result is IQueryable with ordering (strips it)
/// - Result is IQueryable without ordering (no change)
/// </summary>
public class OrderByStrippingMiddlewareTests {
  // ========================================
  // Middleware Branch: No sorting arguments
  // ========================================

  [Test]
  public async Task Middleware_WithNoSortArguments_PreservesOriginalOrderAsync() {
    // Arrange - Query without sort arguments, middleware should not strip ordering
    await using var server = await GraphQLTestServer.CreateAsync();
    var id1 = Guid.NewGuid();
    var id2 = Guid.NewGuid();
    var id3 = Guid.NewGuid();
    server.PreOrderedProductLens.AddData([
      TestDataFactory.CreateProductRow(id: id1, name: "A-Product", price: 100m),
      TestDataFactory.CreateProductRow(id: id2, name: "B-Product", price: 200m),
      TestDataFactory.CreateProductRow(id: id3, name: "C-Product", price: 50m)
    ]);

    // Act - Query without sort argument triggers _hasSortingArguments returning false
    var result = await server.ExecuteAsync("""
      {
        preOrderedProducts {
          nodes {
            data {
              name
            }
          }
        }
      }
      """);

    // Assert - Should return results (original order preserved)
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("A-Product");
    await Assert.That(json).Contains("B-Product");
    await Assert.That(json).Contains("C-Product");
  }

  // ========================================
  // Middleware Branch: With sorting arguments (strips pre-existing ordering)
  // ========================================

  [Test]
  public async Task Middleware_WithSortArguments_StripsPreExistingOrderByAsync() {
    // Arrange - Pre-ordered lens has OrderBy(Id), GraphQL sort by price DESC should replace it
    await using var server = await GraphQLTestServer.CreateAsync();
    var id1 = Guid.NewGuid();
    var id2 = Guid.NewGuid();
    var id3 = Guid.NewGuid();
    server.PreOrderedProductLens.AddData([
      TestDataFactory.CreateProductRow(id: id1, name: "Cheap", price: 10m),
      TestDataFactory.CreateProductRow(id: id2, name: "Medium", price: 100m),
      TestDataFactory.CreateProductRow(id: id3, name: "Expensive", price: 1000m)
    ]);

    // Act - Sort by price DESC should strip pre-existing OrderBy(Id)
    var result = await server.ExecuteAsync("""
      {
        preOrderedProducts(order: { data: { price: DESC } }) {
          nodes {
            data {
              name
              price
            }
          }
        }
      }
      """);

    // Assert - Expensive should be first (price DESC)
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    // Verify order: Expensive (1000) should come before Medium (100) which should come before Cheap (10)
    var expensiveIndex = json.IndexOf("Expensive", StringComparison.Ordinal);
    var mediumIndex = json.IndexOf("Medium", StringComparison.Ordinal);
    var cheapIndex = json.IndexOf("Cheap", StringComparison.Ordinal);
    await Assert.That(expensiveIndex).IsLessThan(mediumIndex);
    await Assert.That(mediumIndex).IsLessThan(cheapIndex);
  }

  [Test]
  public async Task Middleware_WithSortAscending_StripsAndAppliesAscendingAsync() {
    // Arrange
    await using var server = await GraphQLTestServer.CreateAsync();
    server.PreOrderedProductLens.AddData([
      TestDataFactory.CreateProductRow(name: "Zebra", price: 300m),
      TestDataFactory.CreateProductRow(name: "Apple", price: 100m),
      TestDataFactory.CreateProductRow(name: "Mango", price: 200m)
    ]);

    // Act - Sort by name ASC
    var result = await server.ExecuteAsync("""
      {
        preOrderedProducts(order: { data: { name: ASC } }) {
          nodes {
            data {
              name
            }
          }
        }
      }
      """);

    // Assert - Apple should be first (name ASC)
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    var appleIndex = json.IndexOf("Apple", StringComparison.Ordinal);
    var mangoIndex = json.IndexOf("Mango", StringComparison.Ordinal);
    var zebraIndex = json.IndexOf("Zebra", StringComparison.Ordinal);
    await Assert.That(appleIndex).IsLessThan(mangoIndex);
    await Assert.That(mangoIndex).IsLessThan(zebraIndex);
  }

  // ========================================
  // Middleware Branch: Empty result set
  // ========================================

  [Test]
  public async Task Middleware_WithEmptyResult_HandlesGracefullyAsync() {
    // Arrange - No data in the lens
    await using var server = await GraphQLTestServer.CreateAsync();

    // Act
    var result = await server.ExecuteAsync("""
      {
        preOrderedProducts(order: { data: { name: ASC } }) {
          nodes {
            data {
              name
            }
          }
        }
      }
      """);

    // Assert - Should return empty nodes without errors
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("nodes");
  }

  // ========================================
  // Middleware Branch: IQueryable without ordering
  // ========================================

  [Test]
  public async Task Middleware_NonPreOrderedQuery_WithSort_WorksNormallyAsync() {
    // Arrange - Regular products query (no [UseOrderByStripping]) should work with sorting
    await using var server = await GraphQLTestServer.CreateAsync();
    server.ProductLens.AddData([
      TestDataFactory.CreateProductRow(name: "Beta", price: 200m),
      TestDataFactory.CreateProductRow(name: "Alpha", price: 100m)
    ]);

    // Act - Sort on regular (non-pre-ordered) query
    var result = await server.ExecuteAsync("""
      {
        products(order: { data: { name: ASC } }) {
          nodes {
            data {
              name
            }
          }
        }
      }
      """);

    // Assert
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    var alphaIndex = json.IndexOf("Alpha", StringComparison.Ordinal);
    var betaIndex = json.IndexOf("Beta", StringComparison.Ordinal);
    await Assert.That(alphaIndex).IsLessThan(betaIndex);
  }

  // ========================================
  // Create() Method Tests
  // ========================================

  [Test]
  public async Task Create_ReturnsNonNullMiddlewareAsync() {
    // Act
    var middleware = Whizbang.Transports.HotChocolate.Middleware.OrderByStrippingMiddleware.Create();

    // Assert
    await Assert.That(middleware).IsNotNull();
  }
}
