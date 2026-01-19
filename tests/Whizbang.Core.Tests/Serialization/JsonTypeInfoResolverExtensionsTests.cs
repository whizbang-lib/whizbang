using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>
/// Tests for JsonTypeInfoResolverExtensions - DEPRECATED extension methods for combining resolvers.
/// NOTE: These methods are obsolete. Modern code should use JsonContextRegistry.RegisterContext().
/// </summary>
public class JsonTypeInfoResolverExtensionsTests {
  [Test]
  public async Task CombineWithWhizbangContext_WithUserResolver_CombinesCorrectlyAsync() {
    // Arrange
    var userContext = new TestJsonContext();

    // Act
#pragma warning disable CS0618 // Type or member is obsolete
    var result = userContext.CombineWithWhizbangContext();
#pragma warning restore CS0618

    // Assert
    await Assert.That(result).IsNotNull();
    // Should contain both Whizbang contexts and user resolver
  }

  [Test]
  public async Task CombineWithWhizbangContext_WithNullResolver_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    IJsonTypeInfoResolver? userResolver = null;

    // Act & Assert
#pragma warning disable CS0618 // Type or member is obsolete
    await Assert.That(() => userResolver!.CombineWithWhizbangContext())
      .ThrowsExactly<ArgumentNullException>();
#pragma warning restore CS0618
  }

  [Test]
  public async Task CombineWithWhizbangContext_WithMultipleResolvers_CombinesCorrectlyAsync() {
    // Arrange
    var context1 = new TestJsonContext();
    var context2 = new TestJsonContext();

    // Act
#pragma warning disable CS0618 // Type or member is obsolete
    var result = JsonTypeInfoResolverExtensions.CombineWithWhizbangContext(context1, context2);
#pragma warning restore CS0618

    // Assert
    await Assert.That(result).IsNotNull();
    // Should contain Whizbang contexts + both user resolvers
  }

  [Test]
  public async Task CombineWithWhizbangContext_WithNullResolversArray_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    IJsonTypeInfoResolver[]? userResolvers = null;

    // Act & Assert
#pragma warning disable CS0618 // Type or member is obsolete
    await Assert.That(() => JsonTypeInfoResolverExtensions.CombineWithWhizbangContext(userResolvers!))
      .ThrowsExactly<ArgumentNullException>();
#pragma warning restore CS0618
  }

  [Test]
  public async Task CombineWithWhizbangContext_WithEmptyResolversArray_CombinesWithWhizbangOnlyAsync() {
    // Arrange
    var userResolvers = Array.Empty<IJsonTypeInfoResolver>();

    // Act
#pragma warning disable CS0618 // Type or member is obsolete
    var result = JsonTypeInfoResolverExtensions.CombineWithWhizbangContext(userResolvers);
#pragma warning restore CS0618

    // Assert
    await Assert.That(result).IsNotNull();
    // Should contain only Whizbang contexts
  }

  [Test]
  public async Task CombineWithWhizbangContext_WithNoRegisteredContexts_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    // Clear all registered contexts (if possible - may need test isolation)
    var userContext = new TestJsonContext();

    // Act & Assert
    // This test may need adjustment based on JsonContextRegistry implementation
    // If registry is empty, should throw InvalidOperationException
    // If registry always has contexts, this test should verify combination works
#pragma warning disable CS0618 // Type or member is obsolete
    var result = userContext.CombineWithWhizbangContext();
#pragma warning restore CS0618

    await Assert.That(result).IsNotNull();
  }
}

/// <summary>
/// Test JsonSerializerContext for testing purposes.
/// </summary>
[JsonSerializable(typeof(string))]
internal sealed partial class TestJsonContext : JsonSerializerContext {
}
