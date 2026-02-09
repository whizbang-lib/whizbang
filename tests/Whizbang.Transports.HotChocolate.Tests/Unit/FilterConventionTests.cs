using HotChocolate.Data.Filters;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Transports.HotChocolate;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="WhizbangFilterConvention"/>.
/// Verifies filter convention configuration and registration.
/// </summary>
public class FilterConventionTests {
  [Test]
  public async Task FilterConvention_ShouldExtendDefaultConventionAsync() {
    // Arrange
    var conventionType = typeof(WhizbangFilterConvention);

    // Act
    var baseType = conventionType.BaseType;

    // Assert
    await Assert.That(baseType).IsEqualTo(typeof(FilterConvention));
  }

  [Test]
  public async Task FilterConvention_ShouldBeRegistrableWithHotChocolateAsync() {
    // Arrange & Act
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddFiltering<WhizbangFilterConvention>()
        .BuildSchemaAsync();

    // Assert
    await Assert.That(schema).IsNotNull();
  }

  [Test]
  public async Task FilterConvention_ShouldBePublicClassAsync() {
    // Arrange
    var conventionType = typeof(WhizbangFilterConvention);

    // Act
    var isPublic = conventionType.IsPublic;
    var isClass = conventionType.IsClass;

    // Assert
    await Assert.That(isPublic).IsTrue();
    await Assert.That(isClass).IsTrue();
  }

  [Test]
  public async Task FilterConvention_ShouldNotBeSealedAsync() {
    // Arrange - allow extension
    var conventionType = typeof(WhizbangFilterConvention);

    // Act
    var isSealed = conventionType.IsSealed;

    // Assert
    await Assert.That(isSealed).IsFalse();
  }

  [Test]
  public async Task FilterConvention_ShouldHaveConfigureMethodAsync() {
    // Arrange
    var conventionType = typeof(WhizbangFilterConvention);

    // Act
    var configureMethod = conventionType.GetMethod(
        "Configure",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    // Assert
    await Assert.That(configureMethod).IsNotNull();
  }

  [Test]
  public async Task FilterConvention_WithWhizbangLenses_ShouldRegisterAsync() {
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
  public async Task FilterConvention_ShouldBeChainableWithOtherExtensionsAsync() {
    // Arrange & Act
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddFiltering<WhizbangFilterConvention>()
        .AddSorting()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .BuildSchemaAsync();

    // Assert
    await Assert.That(schema).IsNotNull();
  }

  [Test]
  public async Task FilterConvention_CanBeInstantiatedAsync() {
    // Arrange & Act
    var convention = new WhizbangFilterConvention();

    // Assert
    await Assert.That(convention).IsNotNull();
  }

  [Test]
  public async Task FilterConvention_InheritedFromFilterConvention_ShouldAddDefaultsAsync() {
    // Arrange & Act - The convention inherits AddDefaults behavior
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddFiltering<WhizbangFilterConvention>()
        .BuildSchemaAsync();

    // Assert - Schema builds successfully with defaults
    await Assert.That(schema).IsNotNull();
    var queryType = schema.QueryType;
    await Assert.That(queryType).IsNotNull();
  }
}
