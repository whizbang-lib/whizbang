using System.Text.Json.Serialization;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.HotChocolate;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="PolymorphicTypeExtensions"/>.
/// Verifies polymorphic type registration with HotChocolate GraphQL.
/// </summary>
[Category("Unit")]
[Category("Polymorphic")]
public class PolymorphicTypeExtensionsTests {

  // Test polymorphic base type with JsonDerivedType attributes
  [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
  [JsonDerivedType(typeof(TextFieldSettings), "text")]
  [JsonDerivedType(typeof(NumberFieldSettings), "number")]
  public abstract class AbstractFieldSettings {
    public string FieldName { get; set; } = "";
  }

  public sealed class TextFieldSettings : AbstractFieldSettings {
    public int MaxLength { get; set; }
  }

  public sealed class NumberFieldSettings : AbstractFieldSettings {
    public int MinValue { get; set; }
    public int MaxValue { get; set; }
  }

  // Test type without JsonPolymorphic attribute
  public abstract class NonPolymorphicBase {
    public string Name { get; set; } = "";
  }

  public sealed class ConcreteType : NonPolymorphicBase { }

  [Test]
  public async Task AddPolymorphicType_WithExplicitDerivedTypes_ReturnsBuilderAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var builder = services
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddPolymorphicType<AbstractFieldSettings>(
            typeof(TextFieldSettings),
            typeof(NumberFieldSettings));

    // Assert
    await Assert.That(builder).IsNotNull();
  }

  [Test]
  public async Task AddPolymorphicType_WithExplicitDerivedTypes_BuildsSchemaAsync() {
    // Arrange & Act
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddPolymorphicType<AbstractFieldSettings>(
            typeof(TextFieldSettings),
            typeof(NumberFieldSettings))
        .BuildSchemaAsync();

    // Assert
    await Assert.That(schema).IsNotNull();
  }

  [Test]
  public async Task AddPolymorphicType_WithAutoDiscovery_ReturnsBuilderAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act - Uses parameterless overload that discovers types from [JsonDerivedType] attributes
    var builder = services
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddPolymorphicType<AbstractFieldSettings>();

    // Assert
    await Assert.That(builder).IsNotNull();
  }

  [Test]
  public async Task AddPolymorphicType_WithAutoDiscovery_BuildsSchemaAsync() {
    // Arrange & Act - Uses parameterless overload that discovers types from [JsonDerivedType] attributes
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddPolymorphicType<AbstractFieldSettings>()
        .BuildSchemaAsync();

    // Assert
    await Assert.That(schema).IsNotNull();
  }

  [Test]
  public async Task AddPolymorphicType_WithoutJsonPolymorphicAttribute_ThrowsExceptionAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = services
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"));

    // Act & Assert
    await Assert.That(() => builder.AddPolymorphicType<NonPolymorphicBase>())
        .ThrowsException().WithMessageContaining("JsonPolymorphic");
  }

  [Test]
  public async Task AddPolymorphicType_CanChainWithOtherMethodsAsync() {
    // Arrange & Act
    var schema = await new ServiceCollection()
        .AddGraphQL()
        .AddQueryType(d => d
            .Name("Query")
            .Field("test")
            .Type<StringType>()
            .Resolve("test"))
        .AddWhizbangLenses()
        .AddPolymorphicType<AbstractFieldSettings>(
            typeof(TextFieldSettings),
            typeof(NumberFieldSettings))
        .BuildSchemaAsync();

    // Assert - Chaining should work without issues
    await Assert.That(schema).IsNotNull();
  }
}
