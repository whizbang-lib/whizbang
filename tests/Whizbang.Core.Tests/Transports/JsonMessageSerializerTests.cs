using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Transports;

/// <summary>
/// Tests for JsonMessageSerializer constructor validation.
/// Ensures proper error handling for invalid configurations.
/// </summary>
[Category("Core")]
[Category("Transports")]
public class JsonMessageSerializerTests {
  [Test]
  public async Task Constructor_WithNullTypeInfoResolver_ShouldThrowArgumentExceptionAsync() {
    // Arrange - Create options without TypeInfoResolver (default is null)
    var options = new JsonSerializerOptions();

    // Act & Assert - Should throw because TypeInfoResolver is not configured
    await Assert.That(() => new JsonMessageSerializer(options))
      .ThrowsExactly<ArgumentException>()
      .WithMessageContaining("TypeInfoResolver");
  }

  [Test]
  public async Task Constructor_WithNullOptions_ShouldThrowArgumentNullExceptionAsync() {
    // Act & Assert - Should throw for null options
    await Assert.That(() => new JsonMessageSerializer((JsonSerializerOptions)null!))
      .ThrowsExactly<ArgumentNullException>();
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
}
