using Whizbang.Transports.HotChocolate.Tests.Fixtures;

namespace Whizbang.Transports.HotChocolate.Tests.Integration;

/// <summary>
/// Integration tests for GraphQL query execution with Whizbang lenses.
/// Tests actual GraphQL query execution against in-memory lens data.
/// </summary>
[Category("Integration")]
[Category("GraphQL")]
public class QueryExecutionTests {

  [Test]
  public async Task Query_WithEmptyLens_ReturnsEmptyResultAsync() {
    // Arrange
    await using var server = await GraphQLTestServer.CreateAsync();

    // Act
    var result = await server.ExecuteAsync("""
            {
              orders {
                nodes {
                  id
                }
              }
            }
            """);

    // Assert
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
  }

  [Test]
  public async Task Query_WithData_ReturnsDataAsync() {
    // Arrange
    await using var server = await GraphQLTestServer.CreateAsync();
    var orderId = Guid.NewGuid();
    server.OrderLens.AddData([
        TestDataFactory.CreateOrderRow(id: orderId, customerName: "Alice", status: "Completed")
    ]);

    // Act
    var result = await server.ExecuteAsync("""
            {
              orders {
                nodes {
                  id
                  data {
                    customerName
                    status
                  }
                }
              }
            }
            """);

    // Assert
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("Alice");
    await Assert.That(json).Contains("Completed");
  }

  [Test]
  public async Task Query_WithFiltering_FiltersResultsAsync() {
    // Arrange
    await using var server = await GraphQLTestServer.CreateAsync();
    server.OrderLens.AddData([
        TestDataFactory.CreateOrderRow(customerName: "Alice", status: "Completed"),
        TestDataFactory.CreateOrderRow(customerName: "Bob", status: "Pending"),
        TestDataFactory.CreateOrderRow(customerName: "Charlie", status: "Completed")
    ]);

    // Act
    var result = await server.ExecuteAsync("""
            {
              orders(where: { data: { status: { eq: "Completed" } } }) {
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
    await Assert.That(json).Contains("Alice");
    await Assert.That(json).Contains("Charlie");
    await Assert.That(json).DoesNotContain("Bob");
  }

  [Test]
  public async Task Query_WithSorting_SortsResultsAsync() {
    // Arrange
    await using var server = await GraphQLTestServer.CreateAsync();
    server.OrderLens.AddData([
        TestDataFactory.CreateOrderRow(customerName: "Charlie"),
        TestDataFactory.CreateOrderRow(customerName: "Alice"),
        TestDataFactory.CreateOrderRow(customerName: "Bob")
    ]);

    // Act
    var result = await server.ExecuteAsync("""
            {
              orders(order: { data: { customerName: ASC } }) {
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

    // Check order by finding positions
    var aliceIndex = json.IndexOf("Alice", StringComparison.Ordinal);
    var bobIndex = json.IndexOf("Bob", StringComparison.Ordinal);
    var charlieIndex = json.IndexOf("Charlie", StringComparison.Ordinal);

    await Assert.That(aliceIndex).IsLessThan(bobIndex);
    await Assert.That(bobIndex).IsLessThan(charlieIndex);
  }

  [Test]
  public async Task Query_WithPaging_ReturnsPagedResultsAsync() {
    // Arrange
    await using var server = await GraphQLTestServer.CreateAsync();
    for (int i = 0; i < 25; i++) {
      server.OrderLens.AddData([
          TestDataFactory.CreateOrderRow(customerName: $"Customer{i:D2}")
      ]);
    }

    // Act - Get first page with 5 items
    var result = await server.ExecuteAsync("""
            {
              orders(first: 5) {
                nodes {
                  data {
                    customerName
                  }
                }
                pageInfo {
                  hasNextPage
                  hasPreviousPage
                }
                totalCount
              }
            }
            """);

    // Assert
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("hasNextPage");
    await Assert.That(json).Contains("true");
    await Assert.That(json).Contains("totalCount");
  }

  [Test]
  public async Task Query_Products_ReturnsProductDataAsync() {
    // Arrange
    await using var server = await GraphQLTestServer.CreateAsync();
    server.ProductLens.AddData([
        TestDataFactory.CreateProductRow(name: "Laptop", category: "Electronics", price: 999.99m),
        TestDataFactory.CreateProductRow(name: "Desk", category: "Furniture", price: 299.99m)
    ]);

    // Act
    var result = await server.ExecuteAsync("""
            {
              products {
                nodes {
                  data {
                    name
                    category
                    price
                  }
                }
              }
            }
            """);

    // Assert
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("Laptop");
    await Assert.That(json).Contains("Electronics");
    await Assert.That(json).Contains("Desk");
    await Assert.That(json).Contains("Furniture");
  }

  [Test]
  public async Task Query_WithMetadata_ReturnsMetadataFieldsAsync() {
    // Arrange
    await using var server = await GraphQLTestServer.CreateAsync();
    server.OrderLens.AddData([
        TestDataFactory.CreateOrderRow(customerName: "TestCustomer")
    ]);

    // Act
    var result = await server.ExecuteAsync("""
            {
              orders {
                nodes {
                  id
                  version
                  metadata {
                    eventType
                    correlationId
                  }
                }
              }
            }
            """);

    // Assert
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("eventType");
    await Assert.That(json).Contains("OrderCreated");
    await Assert.That(json).Contains("correlationId");
    await Assert.That(json).Contains("version");
  }

  [Test]
  public async Task Query_FilteredItems_NoSortingOrPagingAsync() {
    // Arrange
    await using var server = await GraphQLTestServer.CreateAsync();
    server.FilterOnlyLens.AddData([
        TestDataFactory.CreateOrderRow(status: "Active"),
        TestDataFactory.CreateOrderRow(status: "Inactive"),
        TestDataFactory.CreateOrderRow(status: "Active")
    ]);

    // Act
    var result = await server.ExecuteAsync("""
            {
              filteredItems(where: { data: { status: { eq: "Active" } } }) {
                id
                data {
                  status
                }
              }
            }
            """);

    // Assert
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("Active");
    await Assert.That(json).DoesNotContain("Inactive");
  }

  [Test]
  public async Task Schema_ContainsExpectedTypesAsync() {
    // Arrange
    await using var server = await GraphQLTestServer.CreateAsync();

    // Act
    var schema = server.Schema;

    // Assert
    await Assert.That(schema.QueryType).IsNotNull();
    await Assert.That(schema.QueryType!.Fields.ContainsField("orders")).IsTrue();
    await Assert.That(schema.QueryType.Fields.ContainsField("products")).IsTrue();
    await Assert.That(schema.QueryType.Fields.ContainsField("filteredItems")).IsTrue();
  }

  [Test]
  public async Task Query_WithComplexFilter_WorksCorrectlyAsync() {
    // Arrange
    await using var server = await GraphQLTestServer.CreateAsync();
    server.ProductLens.AddData([
        TestDataFactory.CreateProductRow(name: "Cheap Laptop", price: 500m),
        TestDataFactory.CreateProductRow(name: "Expensive Laptop", price: 2000m),
        TestDataFactory.CreateProductRow(name: "Cheap Phone", price: 200m),
        TestDataFactory.CreateProductRow(name: "Expensive Phone", price: 1500m)
    ]);

    // Act - Filter products with price >= 1000
    var result = await server.ExecuteAsync("""
            {
              products(where: { data: { price: { gte: 1000 } } }) {
                nodes {
                  data {
                    name
                    price
                  }
                }
              }
            }
            """);

    // Assert
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("Expensive Laptop");
    await Assert.That(json).Contains("Expensive Phone");
    await Assert.That(json).DoesNotContain("Cheap Laptop");
    await Assert.That(json).DoesNotContain("Cheap Phone");
  }

  [Test]
  public async Task Query_WithSortDescending_SortsCorrectlyAsync() {
    // Arrange
    await using var server = await GraphQLTestServer.CreateAsync();
    server.ProductLens.AddData([
        TestDataFactory.CreateProductRow(name: "A-Product", price: 100m),
        TestDataFactory.CreateProductRow(name: "B-Product", price: 200m),
        TestDataFactory.CreateProductRow(name: "C-Product", price: 150m)
    ]);

    // Act - Sort by price descending
    var result = await server.ExecuteAsync("""
            {
              products(order: { data: { price: DESC } }) {
                nodes {
                  data {
                    name
                    price
                  }
                }
              }
            }
            """);

    // Assert
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");

    var bIndex = json.IndexOf("B-Product", StringComparison.Ordinal);
    var cIndex = json.IndexOf("C-Product", StringComparison.Ordinal);
    var aIndex = json.IndexOf("A-Product", StringComparison.Ordinal);

    // B-Product (200) should come before C-Product (150) should come before A-Product (100)
    await Assert.That(bIndex).IsLessThan(cIndex);
    await Assert.That(cIndex).IsLessThan(aIndex);
  }

  #region Pre-Ordered Query Tests

  [Test]
  public async Task Query_WithPreExistingOrderBy_GraphQLSortDescendingReplacesItAsync() {
    // Arrange - Pre-ordered lens returns data sorted by Id ascending
    await using var server = await GraphQLTestServer.CreateAsync();

    // Add data with specific IDs so we can verify ordering
    // IDs are GUIDs but we'll create them in a known order
    var id1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    var id2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    var id3 = Guid.Parse("00000000-0000-0000-0000-000000000003");

    server.PreOrderedProductLens.AddData([
        TestDataFactory.CreateProductRow(id: id1, name: "A-Product", price: 100m),
        TestDataFactory.CreateProductRow(id: id2, name: "B-Product", price: 200m),
        TestDataFactory.CreateProductRow(id: id3, name: "C-Product", price: 150m)
    ]);

    // Act - Sort by price descending (should replace pre-existing OrderBy on Id)
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

    // Assert - Should be sorted by price DESC, not by Id ASC
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");

    var bIndex = json.IndexOf("B-Product", StringComparison.Ordinal);
    var cIndex = json.IndexOf("C-Product", StringComparison.Ordinal);
    var aIndex = json.IndexOf("A-Product", StringComparison.Ordinal);

    // B-Product (200) > C-Product (150) > A-Product (100)
    await Assert.That(bIndex).IsGreaterThanOrEqualTo(0);
    await Assert.That(bIndex).IsLessThan(cIndex);
    await Assert.That(cIndex).IsLessThan(aIndex);
  }

  [Test]
  public async Task Query_WithPreExistingOrderBy_GraphQLSortAscendingReplacesItAsync() {
    // Arrange - Pre-ordered lens returns data sorted by Id ascending
    await using var server = await GraphQLTestServer.CreateAsync();

    var id1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    var id2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    var id3 = Guid.Parse("00000000-0000-0000-0000-000000000003");

    // Add in reverse ID order - pre-ordering should sort by Id
    server.PreOrderedProductLens.AddData([
        TestDataFactory.CreateProductRow(id: id3, name: "C-Product", price: 150m),
        TestDataFactory.CreateProductRow(id: id1, name: "A-Product", price: 100m),
        TestDataFactory.CreateProductRow(id: id2, name: "B-Product", price: 200m)
    ]);

    // Act - Sort by price ascending (should replace pre-existing OrderBy on Id)
    var result = await server.ExecuteAsync("""
            {
              preOrderedProducts(order: { data: { price: ASC } }) {
                nodes {
                  data {
                    name
                    price
                  }
                }
              }
            }
            """);

    // Assert - Should be sorted by price ASC, not by Id ASC
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");

    var aIndex = json.IndexOf("A-Product", StringComparison.Ordinal);
    var cIndex = json.IndexOf("C-Product", StringComparison.Ordinal);
    var bIndex = json.IndexOf("B-Product", StringComparison.Ordinal);

    // A-Product (100) < C-Product (150) < B-Product (200)
    await Assert.That(aIndex).IsGreaterThanOrEqualTo(0);
    await Assert.That(aIndex).IsLessThan(cIndex);
    await Assert.That(cIndex).IsLessThan(bIndex);
  }

  [Test]
  public async Task Query_WithPreExistingOrderBy_NoGraphQLSort_PreservesOriginalOrderAsync() {
    // Arrange - Pre-ordered lens returns data sorted by Id ascending
    await using var server = await GraphQLTestServer.CreateAsync();

    var id1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    var id2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    var id3 = Guid.Parse("00000000-0000-0000-0000-000000000003");

    // Add in reverse ID order - pre-ordering should sort by Id ASC
    server.PreOrderedProductLens.AddData([
        TestDataFactory.CreateProductRow(id: id3, name: "C-Product", price: 150m),
        TestDataFactory.CreateProductRow(id: id1, name: "A-Product", price: 100m),
        TestDataFactory.CreateProductRow(id: id2, name: "B-Product", price: 200m)
    ]);

    // Act - No sorting specified - should preserve pre-existing order
    var result = await server.ExecuteAsync("""
            {
              preOrderedProducts {
                nodes {
                  data {
                    name
                    price
                  }
                }
              }
            }
            """);

    // Assert - Should preserve original OrderBy(Id) - A (id1) < B (id2) < C (id3)
    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");

    var aIndex = json.IndexOf("A-Product", StringComparison.Ordinal);
    var bIndex = json.IndexOf("B-Product", StringComparison.Ordinal);
    var cIndex = json.IndexOf("C-Product", StringComparison.Ordinal);

    // Should be in Id order: A (id1) < B (id2) < C (id3)
    await Assert.That(aIndex).IsGreaterThanOrEqualTo(0);
    await Assert.That(aIndex).IsLessThan(bIndex);
    await Assert.That(bIndex).IsLessThan(cIndex);
  }

  #endregion
}
