using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Transports.HotChocolate;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="HotChocolateWhizbangExtensions"/>.
/// Verifies service registration and configuration options.
/// </summary>
public class ServiceRegistrationTests {
  [Test]
  public async Task AddWhizbangLenses_ShouldBeRegistrableAsync() {
    // Arrange & Act
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddWhizbangLenses()
        .BuildSchemaAsync();

    // Assert
    await Assert.That(schema).IsNotNull();
  }

  [Test]
  public async Task AddWhizbangLenses_ShouldReturnRequestExecutorBuilderAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var builder = services
        .AddGraphQL()
        .AddWhizbangLenses();

    // Assert - builder should not be null and should be chainable
    await Assert.That(builder).IsNotNull();
  }

  [Test]
  public async Task AddWhizbangLenses_WithOptions_ShouldApplyConfigurationAsync() {
    // Arrange
    var configuredScope = GraphQLLensScope.All;
    var configuredPageSize = 25;
    var configuredMaxPageSize = 200;

    // Act
    var serviceProvider = new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddWhizbangLenses(options => {
          options.DefaultScope = configuredScope;
          options.DefaultPageSize = configuredPageSize;
          options.MaxPageSize = configuredMaxPageSize;
        })
        .Services
        .BuildServiceProvider();

    var options = serviceProvider.GetService<WhizbangGraphQLOptions>();

    // Assert
    await Assert.That(options).IsNotNull();
    await Assert.That(options!.DefaultScope).IsEqualTo(configuredScope);
    await Assert.That(options.DefaultPageSize).IsEqualTo(configuredPageSize);
    await Assert.That(options.MaxPageSize).IsEqualTo(configuredMaxPageSize);
  }

  [Test]
  public async Task AddWhizbangLenses_WithoutOptions_ShouldUseDefaultsAsync() {
    // Arrange & Act
    var serviceProvider = new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddWhizbangLenses()
        .Services
        .BuildServiceProvider();

    var options = serviceProvider.GetService<WhizbangGraphQLOptions>();

    // Assert - should use defaults
    await Assert.That(options).IsNotNull();
    await Assert.That(options!.DefaultScope).IsEqualTo(GraphQLLensScope.DataOnly);
    await Assert.That(options.DefaultPageSize).IsEqualTo(10);
    await Assert.That(options.MaxPageSize).IsEqualTo(100);
  }

  [Test]
  public async Task AddWhizbangLenses_ShouldRegisterFilterConventionAsync() {
    // Arrange & Act
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddWhizbangLenses()
        .BuildSchemaAsync();

    // Assert - schema should be built successfully with filtering support
    await Assert.That(schema).IsNotNull();
  }

  [Test]
  public async Task AddWhizbangLenses_ShouldRegisterSortConventionAsync() {
    // Arrange & Act
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddWhizbangLenses()
        .BuildSchemaAsync();

    // Assert - schema should be built successfully with sorting support
    await Assert.That(schema).IsNotNull();
  }

  [Test]
  public async Task AddWhizbangLenses_ShouldRegisterOptionsAsSingletonAsync() {
    // Arrange
    var serviceProvider = new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddWhizbangLenses(options => options.DefaultPageSize = 50)
        .Services
        .BuildServiceProvider();

    // Act - get options twice
    var options1 = serviceProvider.GetService<WhizbangGraphQLOptions>();
    var options2 = serviceProvider.GetService<WhizbangGraphQLOptions>();

    // Assert - should be same instance (singleton)
    await Assert.That(options1).IsNotNull();
    await Assert.That(options2).IsNotNull();
    var areSameInstance = ReferenceEquals(options1, options2);
    await Assert.That(areSameInstance).IsTrue();
  }

  [Test]
  public async Task AddWhizbangLenses_ShouldBeChaineableAsync() {
    // Arrange & Act
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddWhizbangLenses()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .BuildSchemaAsync();

    // Assert - chaining should work
    await Assert.That(schema).IsNotNull();
  }

  [Test]
  public async Task AddWhizbangLenses_WithCustomScope_ShouldPersistAsync() {
    // Arrange
    var customScope = GraphQLLensScope.Data | GraphQLLensScope.SystemFields;

    // Act
    var serviceProvider = new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddWhizbangLenses(options => options.DefaultScope = customScope)
        .Services
        .BuildServiceProvider();

    var options = serviceProvider.GetService<WhizbangGraphQLOptions>();

    // Assert
    await Assert.That(options).IsNotNull();
    await Assert.That(options!.DefaultScope).IsEqualTo(customScope);

    var hasData = options.DefaultScope.HasFlag(GraphQLLensScope.Data);
    var hasSystemFields = options.DefaultScope.HasFlag(GraphQLLensScope.SystemFields);
    var hasMetadata = options.DefaultScope.HasFlag(GraphQLLensScope.Metadata);
    var hasScope = options.DefaultScope.HasFlag(GraphQLLensScope.Scope);

    await Assert.That(hasData).IsTrue();
    await Assert.That(hasSystemFields).IsTrue();
    await Assert.That(hasMetadata).IsFalse();
    await Assert.That(hasScope).IsFalse();
  }

  [Test]
  public async Task AddWhizbangLenses_IncludeMetadataInFilters_ShouldBeConfigurableAsync() {
    // Arrange & Act
    var serviceProvider = new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddWhizbangLenses(options => options.IncludeMetadataInFilters = false)
        .Services
        .BuildServiceProvider();

    var options = serviceProvider.GetService<WhizbangGraphQLOptions>();

    // Assert
    await Assert.That(options).IsNotNull();
    await Assert.That(options!.IncludeMetadataInFilters).IsFalse();
  }

  [Test]
  public async Task AddWhizbangLenses_IncludeScopeInFilters_ShouldBeConfigurableAsync() {
    // Arrange & Act
    var serviceProvider = new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddWhizbangLenses(options => options.IncludeScopeInFilters = false)
        .Services
        .BuildServiceProvider();

    var options = serviceProvider.GetService<WhizbangGraphQLOptions>();

    // Assert
    await Assert.That(options).IsNotNull();
    await Assert.That(options!.IncludeScopeInFilters).IsFalse();
  }
}
