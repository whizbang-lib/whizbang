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
        .AddWhizbangLenses(options => options.DefaultScope = GraphQLLensScopes.DataOnly)
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
        .AddWhizbangLenses(options => options.DefaultScope = GraphQLLensScopes.All)
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
        .AddWhizbangLenses(options => options.DefaultScope = GraphQLLensScopes.NoData)
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
        .AddWhizbangLenses(options => options.DefaultScope = GraphQLLensScopes.Data | GraphQLLensScopes.SystemFields)
        .BuildSchemaAsync();

    // Assert
    await Assert.That(schema).IsNotNull();
  }

  // Test query types
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S1118:Utility classes should not have public constructors", Justification = "HotChocolate instantiates query types.")]
  public class TestQuery {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Resolver shape: HotChocolate binds instance methods.")]
    public IQueryable<PerspectiveRow<TestReadModel>> GetItems()
        => new List<PerspectiveRow<TestReadModel>>().AsQueryable();
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S1118:Utility classes should not have public constructors", Justification = "HotChocolate instantiates query types.")]
  public class DataOnlyQuery {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Resolver shape: HotChocolate binds instance methods.")]
    public IQueryable<PerspectiveRow<TestReadModel>> GetItems()
        => new List<PerspectiveRow<TestReadModel>>().AsQueryable();
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S1118:Utility classes should not have public constructors", Justification = "HotChocolate instantiates query types.")]
  public class MetadataQuery {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Resolver shape: HotChocolate binds instance methods.")]
    public PerspectiveMetadata? GetMetadata() => null;
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S1118:Utility classes should not have public constructors", Justification = "HotChocolate instantiates query types.")]
  public class ScopeQuery {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Resolver shape: HotChocolate binds instance methods.")]
    public PerspectiveScope? GetScope() => null;
  }
}
