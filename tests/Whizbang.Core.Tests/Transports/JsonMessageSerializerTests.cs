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
}
