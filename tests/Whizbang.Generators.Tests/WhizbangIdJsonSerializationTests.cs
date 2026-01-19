using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for WhizbangId JSON serialization using generated JsonContext and converters.
/// Verifies that WhizbangId types can be serialized and deserialized correctly with source generation.
/// </summary>
public class WhizbangIdJsonSerializationTests {
  /// <summary>
  /// Test that generated WhizbangIdJsonContext provides JsonTypeInfo for WhizbangId types.
  /// This is the critical fix - the resolver must return JsonTypeInfo, not null.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task WhizbangIdJsonContext_WithProductId_ProvidesJsonTypeInfoAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should generate WhizbangIdJsonContext.g.cs
    var contextSource = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangIdJsonContext.g.cs");
    await Assert.That(contextSource).IsNotNull();

    // Assert - Should implement IJsonTypeInfoResolver
    await Assert.That(contextSource!).Contains("IJsonTypeInfoResolver");
    await Assert.That(contextSource).Contains("public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)");

    // Assert - Should create JsonTypeInfo using JsonMetadataServices.CreateValueInfo
    await Assert.That(contextSource).Contains("JsonMetadataServices.CreateValueInfo");

    // Assert - Should NOT just return null (the bug we're fixing)
    await Assert.That(contextSource).DoesNotContain("return null; // WhizbangId types use custom JSON converters");

    // Assert - Should create converter instance and use it
    await Assert.That(contextSource).Contains("var converter = new");
    await Assert.That(contextSource).Contains("var jsonTypeInfo = JsonMetadataServices.CreateValueInfo");
  }

  /// <summary>
  /// Test that generated JsonContext handles multiple WhizbangId types.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task WhizbangIdJsonContext_WithMultipleIdTypes_ProvidesJsonTypeInfoForAllAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;

            [WhizbangId]
            public readonly partial struct OrderId;

            [WhizbangId]
            public readonly partial struct CustomerId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should generate WhizbangIdJsonContext with all three types
    var contextSource = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangIdJsonContext.g.cs");
    await Assert.That(contextSource).IsNotNull();

    // Assert - Should handle ProductId
    await Assert.That(contextSource!).Contains("if (type == typeof(MyApp.Domain.ProductId))");
    await Assert.That(contextSource).Contains("new MyApp.Domain.ProductIdJsonConverter()");
    await Assert.That(contextSource).Contains("JsonMetadataServices.CreateValueInfo<MyApp.Domain.ProductId>");

    // Assert - Should handle OrderId
    await Assert.That(contextSource).Contains("if (type == typeof(MyApp.Domain.OrderId))");
    await Assert.That(contextSource).Contains("new MyApp.Domain.OrderIdJsonConverter()");
    await Assert.That(contextSource).Contains("JsonMetadataServices.CreateValueInfo<MyApp.Domain.OrderId>");

    // Assert - Should handle CustomerId
    await Assert.That(contextSource).Contains("if (type == typeof(MyApp.Domain.CustomerId))");
    await Assert.That(contextSource).Contains("new MyApp.Domain.CustomerIdJsonConverter()");
    await Assert.That(contextSource).Contains("JsonMetadataServices.CreateValueInfo<MyApp.Domain.CustomerId>");
  }

  /// <summary>
  /// Test that generated JsonConverter serializes WhizbangId using UUIDv7 format.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task JsonConverter_Serialization_UsesUuidv7FormatAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should generate converter with Write method
    var converterSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductIdJsonConverter.g.cs");
    await Assert.That(converterSource).IsNotNull();

    // Assert - Write method should convert to Uuid7
    await Assert.That(converterSource!).Contains("public override void Write(Utf8JsonWriter writer, ProductId value, JsonSerializerOptions options)");
    await Assert.That(converterSource).Contains("var uuid7 = new Uuid7(value.Value)");
    await Assert.That(converterSource).Contains("writer.WriteStringValue(uuid7.ToString())");
  }

  /// <summary>
  /// Test that generated JsonConverter deserializes WhizbangId from UUIDv7 format.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task JsonConverter_Deserialization_ParsesUuidv7FormatAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should generate converter with Read method
    var converterSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductIdJsonConverter.g.cs");
    await Assert.That(converterSource).IsNotNull();

    // Assert - Read method should parse Uuid7
    await Assert.That(converterSource!).Contains("public override ProductId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)");
    await Assert.That(converterSource).Contains("var uuid7String = reader.GetString()");
    await Assert.That(converterSource).Contains("var uuid7 = Uuid7.Parse(uuid7String)");
    await Assert.That(converterSource).Contains("return ProductId.From(uuid7.ToGuid())");
  }

  /// <summary>
  /// Test that WhizbangIdJsonContext has correct using statements for JSON serialization.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task WhizbangIdJsonContext_HasRequiredUsingStatementsAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should have required using statements
    var contextSource = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangIdJsonContext.g.cs");
    await Assert.That(contextSource).IsNotNull();

    await Assert.That(contextSource!).Contains("using System;");
    await Assert.That(contextSource).Contains("using System.Text.Json;");
    await Assert.That(contextSource).Contains("using System.Text.Json.Serialization;");
    await Assert.That(contextSource).Contains("using System.Text.Json.Serialization.Metadata;");
  }

  /// <summary>
  /// Test that WhizbangIdJsonContext is in the correct namespace.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task WhizbangIdJsonContext_IsInWhizbangCoreGeneratedNamespaceAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should be in Whizbang.Core.Generated namespace
    var contextSource = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangIdJsonContext.g.cs");
    await Assert.That(contextSource).IsNotNull();
    await Assert.That(contextSource!).Contains("namespace TestAssembly.Generated");
  }

  /// <summary>
  /// Test that WhizbangIdJsonContext has a singleton Default instance.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task WhizbangIdJsonContext_HasDefaultSingletonAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should have Default singleton
    var contextSource = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangIdJsonContext.g.cs");
    await Assert.That(contextSource).IsNotNull();
    await Assert.That(contextSource!).Contains("public static WhizbangIdJsonContext Default { get; } = new()");
  }

  /// <summary>
  /// Test that each WhizbangId type gets its own dedicated JSON converter.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithMultipleIdTypes_GeneratesIndependentConvertersAsync() {
    // Arrange
    var source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct ProductId;

            [WhizbangId]
            public readonly partial struct OrderId;
            """;

    // Act
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    // Assert - Should generate separate converters
    var productConverterSource = GeneratorTestHelper.GetGeneratedSource(result, "ProductIdJsonConverter.g.cs");
    var orderConverterSource = GeneratorTestHelper.GetGeneratedSource(result, "OrderIdJsonConverter.g.cs");

    await Assert.That(productConverterSource).IsNotNull();
    await Assert.That(orderConverterSource).IsNotNull();

    // Assert - Each converter should be type-specific
    await Assert.That(productConverterSource!).Contains("class ProductIdJsonConverter : JsonConverter<ProductId>");
    await Assert.That(orderConverterSource!).Contains("class OrderIdJsonConverter : JsonConverter<OrderId>");
  }
}
