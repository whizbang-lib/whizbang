using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Tests for JsonMessageSerializer constructor validation and converter integration.
/// Ensures proper error handling for invalid configurations.
/// </summary>
[Category("Core")]
[Category("Transports")]
public class JsonMessageSerializerTests {
  [Test]
  public async Task Constructor_WithNullTypeInfoResolver_ShouldThrowArgumentExceptionAsync() {
    // Arrange - Create options without TypeInfoResolver (default is null)
    var options = new JsonSerializerOptions();

    // Act - Call constructor directly to ensure coverage is collected
    ArgumentException? caughtException = null;
    try {
      _ = new JsonMessageSerializer(options);
    } catch (ArgumentException ex) {
      caughtException = ex;
    }

    // Assert - Should have thrown ArgumentException with correct message
    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("TypeInfoResolver");
  }

  [Test]
  public async Task Constructor_WithNullOptions_ShouldThrowArgumentNullExceptionAsync() {
    // Act - Call constructor directly to ensure coverage is collected
    ArgumentNullException? caughtException = null;
    try {
      _ = new JsonMessageSerializer((JsonSerializerOptions)null!);
    } catch (ArgumentNullException ex) {
      caughtException = ex;
    }

    // Assert - Should have thrown ArgumentNullException
    await Assert.That(caughtException).IsNotNull();
  }

  [Test]
  public async Task Constructor_WithValidTypeInfoResolver_ShouldSucceedAsync() {
    // Arrange - Create options with a valid TypeInfoResolver
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    // Act - Should not throw
    var serializer = new JsonMessageSerializer(options);

    // Assert - Serializer was created successfully
    await Assert.That(serializer).IsNotNull();
  }

  [Test]
  public async Task Constructor_WithValidOptions_ShouldAddRequiredConvertersAsync() {
    // Arrange - Create options with TypeInfoResolver but no converters
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    // Act
    _ = new JsonMessageSerializer(options);

    // Assert - Required converters should be added
    var hasMessageIdConverter = options.Converters.Any(c => c is MessageIdConverter);
    var hasCorrelationIdConverter = options.Converters.Any(c => c is CorrelationIdConverter);
    var hasEnumConverter = options.Converters.Any(c => c is JsonStringEnumConverter);

    await Assert.That(hasMessageIdConverter).IsTrue();
    await Assert.That(hasCorrelationIdConverter).IsTrue();
    await Assert.That(hasEnumConverter).IsTrue();
  }

  [Test]
  public async Task Constructor_WithExistingConverters_ShouldNotDuplicateAsync() {
    // Arrange - Create options with converters already present
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    options.Converters.Add(new MessageIdConverter());
    options.Converters.Add(new CorrelationIdConverter());
    var initialCount = options.Converters.Count;

    // Act
    _ = new JsonMessageSerializer(options);

    // Assert - Should not add duplicate converters for MessageId and CorrelationId
    var messageIdConverterCount = options.Converters.Count(c => c is MessageIdConverter);
    var correlationIdConverterCount = options.Converters.Count(c => c is CorrelationIdConverter);

    await Assert.That(messageIdConverterCount).IsEqualTo(1);
    await Assert.That(correlationIdConverterCount).IsEqualTo(1);
  }

  [Test]
  public async Task Constructor_WithNullContext_ShouldThrowArgumentNullExceptionAsync() {
    // Act - Call constructor with null context
    ArgumentNullException? caughtException = null;
    try {
      _ = new JsonMessageSerializer((JsonSerializerContext)null!);
    } catch (ArgumentNullException ex) {
      caughtException = ex;
    }

    // Assert
    await Assert.That(caughtException).IsNotNull();
  }

  [Test]
  public async Task Constructor_WithValidTypeInfoResolverChain_ShouldSucceedAsync() {
    // Arrange - Create options with TypeInfoResolverChain
    var options = new JsonSerializerOptions();
    options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());

    // Act - Should not throw
    var serializer = new JsonMessageSerializer(options);

    // Assert
    await Assert.That(serializer).IsNotNull();
  }

  [Test]
  public async Task Constructor_WithOptionsWithExistingEnumConverter_ShouldNotDuplicateAsync() {
    // Arrange
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    options.Converters.Add(new JsonStringEnumConverter());
    var initialConverterCount = options.Converters.Count(c => c is JsonStringEnumConverter);

    // Act
    _ = new JsonMessageSerializer(options);

    // Assert - Should not add another JsonStringEnumConverter
    var finalConverterCount = options.Converters.Count(c => c is JsonStringEnumConverter);
    await Assert.That(finalConverterCount).IsEqualTo(initialConverterCount);
  }

  [Test]
  public async Task Constructor_WithValidOptions_ShouldAddMetadataConverterAsync() {
    // Arrange
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    // Act
    _ = new JsonMessageSerializer(options);

    // Assert - MetadataConverter should be added (it's internal so check by CanConvert)
    var hasMetadataConverter = options.Converters.Any(c =>
        c.CanConvert(typeof(IReadOnlyDictionary<string, JsonElement>)));

    await Assert.That(hasMetadataConverter).IsTrue();
  }

  [Test]
  public async Task Constructor_WithEmptyOptionsTypeInfoResolver_ShouldThrowArgumentExceptionAsync() {
    // Arrange - Create options with an empty TypeInfoResolverChain
    var options = new JsonSerializerOptions();
    // Don't add any resolver, but make sure it's not null by accessing the chain
    _ = options.TypeInfoResolverChain.Count; // Just accessing to ensure it exists but is empty

    // Act & Assert
    ArgumentException? caughtException = null;
    try {
      _ = new JsonMessageSerializer(options);
    } catch (ArgumentException ex) {
      caughtException = ex;
    }

    await Assert.That(caughtException).IsNotNull();
    await Assert.That(caughtException!.Message).Contains("TypeInfoResolver");
  }
}
