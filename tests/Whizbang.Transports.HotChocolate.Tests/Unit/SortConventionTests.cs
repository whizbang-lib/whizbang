using HotChocolate.Data.Sorting;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Transports.HotChocolate;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="WhizbangSortConvention"/>.
/// Verifies sort convention configuration and registration.
/// </summary>
public class SortConventionTests {
  [Test]
  public async Task SortConvention_ShouldExtendDefaultConventionAsync() {
    // Arrange
    var conventionType = typeof(WhizbangSortConvention);

    // Act
    var baseType = conventionType.BaseType;

    // Assert
    await Assert.That(baseType).IsEqualTo(typeof(SortConvention));
  }

  [Test]
  public async Task SortConvention_ShouldBeRegistrableWithHotChocolateAsync() {
    // Arrange & Act
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddSorting<WhizbangSortConvention>()
        .BuildSchemaAsync();

    // Assert
    await Assert.That(schema).IsNotNull();
  }

  [Test]
  public async Task SortConvention_ShouldBePublicClassAsync() {
    // Arrange
    var conventionType = typeof(WhizbangSortConvention);

    // Act
    var isPublic = conventionType.IsPublic;
    var isClass = conventionType.IsClass;

    // Assert
    await Assert.That(isPublic).IsTrue();
    await Assert.That(isClass).IsTrue();
  }

  [Test]
  public async Task SortConvention_ShouldNotBeSealedAsync() {
    // Arrange - allow extension
    var conventionType = typeof(WhizbangSortConvention);

    // Act
    var isSealed = conventionType.IsSealed;

    // Assert
    await Assert.That(isSealed).IsFalse();
  }

  [Test]
  public async Task SortConvention_ShouldHaveConfigureMethodAsync() {
    // Arrange
    var conventionType = typeof(WhizbangSortConvention);

    // Act
    var configureMethod = conventionType.GetMethod(
        "Configure",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    // Assert
    await Assert.That(configureMethod).IsNotNull();
  }

  [Test]
  public async Task SortConvention_WithWhizbangLenses_ShouldRegisterAsync() {
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
  public async Task SortConvention_ShouldBeChainableWithOtherExtensionsAsync() {
    // Arrange & Act
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddSorting<WhizbangSortConvention>()
        .AddFiltering()
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
  public async Task SortConvention_CanBeInstantiatedAsync() {
    // Arrange & Act
    var convention = new WhizbangSortConvention();

    // Assert
    await Assert.That(convention).IsNotNull();
  }

  [Test]
  public async Task SortConvention_InheritedFromSortConvention_ShouldAddDefaultsAsync() {
    // Arrange & Act - The convention inherits AddDefaults behavior
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddSorting<WhizbangSortConvention>()
        .BuildSchemaAsync();

    // Assert - Schema builds successfully with defaults
    await Assert.That(schema).IsNotNull();
    var queryType = schema.QueryType;
    await Assert.That(queryType).IsNotNull();
  }
}
