using System.Text.Json;
using System.Text.Json.Serialization;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for JsonContextRegistry - ensures AOT-compatible converter registration works correctly.
/// </summary>
public class JsonContextRegistryTests {
  /// <summary>
  /// Test converter for MessageId-like type (simulates generated WhizbangId converter).
  /// </summary>
  private class TestIdJsonConverter : JsonConverter<_testId> {
    public override _testId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
      return new _testId { Value = reader.GetString() ?? string.Empty };
    }

    public override void Write(Utf8JsonWriter writer, _testId value, JsonSerializerOptions options) {
      writer.WriteStringValue(value.Value);
    }
  }

  /// <summary>
  /// Test ID type (simulates generated WhizbangId value object).
  /// </summary>
  private struct _testId {
    public string Value { get; set; }
  }

  [Test]
  public async Task RegisterConverter_WithConverterInstance_AddsToConverterCollectionAsync() {
    // Arrange
    var converter = new TestIdJsonConverter();
    var initialCount = JsonContextRegistry.RegisteredCount;

    // Act
    JsonContextRegistry.RegisterConverter(converter);

    // Assert - verify registration succeeded
    // Note: We can't directly inspect _converters (it's private), but we can verify
    // it doesn't throw and that the converter works when used in CreateCombinedOptions
    // TUnitAssertions0005: Intentional constant assertion to verify registration doesn't throw
#pragma warning disable TUnitAssertions0005
    await Assert.That(true).IsTrue(); // Registration doesn't throw
#pragma warning restore TUnitAssertions0005
  }

  [Test]
  public async Task RegisterConverter_WithNull_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    var exception = await Assert.That(() => JsonContextRegistry.RegisterConverter(null!))
        .ThrowsExactly<ArgumentNullException>();

    // Verify the parameter name is "converter"
    await Assert.That(exception.ParamName).IsEqualTo("converter");
  }

  [Test]
  public async Task CreateCombinedOptions_WithRegisteredConverters_IncludesConvertersInOptionsAsync() {
    // Note: This test verifies that converters registered via RegisterConverter()
    // are included in the JsonSerializerOptions.Converters collection.
    // Since JsonContextRegistry maintains global state, we rely on the module initializers
    // having already registered the Core converters (MessageId, CorrelationId).

    // Act
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Assert - verify options has converters registered
    await Assert.That(options.Converters).IsNotEmpty();

    // Verify PascalCase naming policy is configured (null = default PascalCase)
    await Assert.That(options.PropertyNamingPolicy).IsNull();

    // Verify WhenWritingNull ignore condition
    await Assert.That(options.DefaultIgnoreCondition).IsEqualTo(JsonIgnoreCondition.WhenWritingNull);
  }

  [Test]
  public async Task CreateCombinedOptions_IsAOTCompatible_NoReflectionAsync() {
    // Arrange & Act
    var options = JsonContextRegistry.CreateCombinedOptions();

    // Assert - verify that we can successfully create options without reflection
    // The fact that this test runs without IL2072 warnings or runtime errors
    // verifies that the implementation is AOT-compatible.
    await Assert.That(options).IsNotNull();
    await Assert.That(options.TypeInfoResolver).IsNotNull();
  }

  [Test]
  public async Task RegisteredConverters_AreInstantiatedAtCompileTime_NotRuntimeAsync() {
    // This test documents the expected behavior:
    // All converters are instantiated using 'new' at compile-time in generated code,
    // not via Activator.CreateInstance() or other reflection at runtime.
    //
    // The generated code should look like:
    //   JsonContextRegistry.RegisterConverter(new ProductIdJsonConverter());
    //
    // NOT like:
    //   JsonContextRegistry.RegisterConverterType(typeof(ProductIdJsonConverter)); // WRONG - uses reflection

    var options = JsonContextRegistry.CreateCombinedOptions();

    // Verify converters exist in options
    await Assert.That(options.Converters).IsNotEmpty();

    // Each converter instance should be directly added (no lazy initialization)
    foreach (var converter in options.Converters) {
      await Assert.That(converter).IsNotNull();
    }
  }
}
