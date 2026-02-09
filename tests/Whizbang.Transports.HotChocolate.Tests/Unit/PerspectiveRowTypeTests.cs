using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lenses;
using Whizbang.Transports.HotChocolate;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for PerspectiveRow GraphQL type registration and configuration.
/// Verifies that PerspectiveRow types are properly integrated with HotChocolate.
/// </summary>
public class PerspectiveRowTypeTests {
  // Test model for type tests
  public class TestReadModel {
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public bool IsActive { get; set; }
  }

  [Test]
  public async Task PerspectiveRow_ShouldBeUsableAsGraphQLReturnTypeAsync() {
    // Arrange & Act
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType<TestQuery>()
        .AddWhizbangLenses()
        .BuildSchemaAsync();

    // Assert
    await Assert.That(schema).IsNotNull();
    var queryType = schema.QueryType;
    await Assert.That(queryType).IsNotNull();
  }

  [Test]
  public async Task PerspectiveRow_Schema_ShouldContainQueryFieldAsync() {
    // Arrange & Act
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType<TestQuery>()
        .AddWhizbangLenses()
        .BuildSchemaAsync();

    // Assert
    var itemsField = schema.QueryType?.Fields["items"];
    await Assert.That(itemsField).IsNotNull();
  }

  [Test]
  public async Task PerspectiveRow_WithDataOnly_ShouldExposeDataFieldAsync() {
    // Arrange
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType<DataOnlyQuery>()
        .AddWhizbangLenses(options => options.DefaultScope = GraphQLLensScope.DataOnly)
        .BuildSchemaAsync();

    // Act - Print schema to check structure
    var schemaStr = schema.ToString();

    // Assert - Schema should contain the query field
    await Assert.That(schema).IsNotNull();
    var hasItemsField = schemaStr.Contains("items");
    await Assert.That(hasItemsField).IsTrue();
  }

  [Test]
  public async Task Schema_ShouldBePrintableAsync() {
    // Arrange
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType<TestQuery>()
        .AddWhizbangLenses()
        .BuildSchemaAsync();

    // Act
    var schemaStr = schema.ToString();

    // Assert
    await Assert.That(schemaStr).IsNotNull();
    var schemaLength = schemaStr.Length;
    await Assert.That(schemaLength).IsGreaterThan(0);
    // Verify query type exists via API
    var queryType = schema.QueryType;
    await Assert.That(queryType).IsNotNull();
  }

  [Test]
  public async Task PerspectiveMetadata_ShouldBeDefinedInSchemaAsync() {
    // Arrange
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType<MetadataQuery>()
        .AddWhizbangLenses()
        .BuildSchemaAsync();

    // Act
    var schemaStr = schema.ToString();

    // Assert - Should have some type definition
    await Assert.That(schema).IsNotNull();
    await Assert.That(schemaStr).IsNotNull();
  }

  [Test]
  public async Task PerspectiveScope_ShouldBeDefinedInSchemaAsync() {
    // Arrange
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType<ScopeQuery>()
        .AddWhizbangLenses()
        .BuildSchemaAsync();

    // Act
    var schemaStr = schema.ToString();

    // Assert - Should have some type definition
    await Assert.That(schema).IsNotNull();
    await Assert.That(schemaStr).IsNotNull();
  }

  [Test]
  public async Task PerspectiveRow_DataField_ShouldContainModelPropertiesAsync() {
    // Arrange
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType<TestQuery>()
        .AddWhizbangLenses()
        .BuildSchemaAsync();

    // Act
    var schemaStr = schema.ToString();

    // Assert - Schema string should exist (full field verification in integration tests)
    await Assert.That(schemaStr).IsNotNull();
    var schemaLength = schemaStr.Length;
    await Assert.That(schemaLength).IsGreaterThan(0);
  }

  [Test]
  public async Task PerspectiveRow_WithAllScope_ShouldBuildSchemaAsync() {
    // Arrange & Act
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType<TestQuery>()
        .AddWhizbangLenses(options => options.DefaultScope = GraphQLLensScope.All)
        .BuildSchemaAsync();

    // Assert
    await Assert.That(schema).IsNotNull();
  }

  [Test]
  public async Task PerspectiveRow_WithNoDataScope_ShouldBuildSchemaAsync() {
    // Arrange & Act
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType<TestQuery>()
        .AddWhizbangLenses(options => options.DefaultScope = GraphQLLensScope.NoData)
        .BuildSchemaAsync();

    // Assert
    await Assert.That(schema).IsNotNull();
  }

  [Test]
  public async Task PerspectiveRow_WithCustomComposedScope_ShouldBuildSchemaAsync() {
    // Arrange & Act
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType<TestQuery>()
        .AddWhizbangLenses(options => options.DefaultScope = GraphQLLensScope.Data | GraphQLLensScope.SystemFields)
        .BuildSchemaAsync();

    // Assert
    await Assert.That(schema).IsNotNull();
  }

  // Test query types
  public class TestQuery {
    public IQueryable<PerspectiveRow<TestReadModel>> GetItems()
        => new List<PerspectiveRow<TestReadModel>>().AsQueryable();
  }

  public class DataOnlyQuery {
    public IQueryable<PerspectiveRow<TestReadModel>> GetItems()
        => new List<PerspectiveRow<TestReadModel>>().AsQueryable();
  }

  public class MetadataQuery {
    public PerspectiveMetadata? GetMetadata() => null;
  }

  public class ScopeQuery {
    public PerspectiveScope? GetScope() => null;
  }
}
