using Whizbang.Core.Generated;

namespace Whizbang.Core;

/// <summary>
/// Resolves stream keys from events using [StreamKey] attribute.
/// Uses source-generated code for zero-reflection AOT compatibility.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/StreamKeyResolutionTests.cs</tests>
public static class StreamKeyResolver {
  /// <summary>
  /// Resolves the stream key from an event.
  /// Looks for a property or parameter marked with [StreamKey] attribute.
  /// </summary>
  /// <param name="event">The event to resolve the stream key from</param>
  /// <returns>The stream key as a string</returns>
  /// <exception cref="InvalidOperationException">
  /// Thrown when no [StreamKey] attribute is found, or when the stream key value is null or empty
  /// </exception>
  /// <tests>tests/Whizbang.Core.Tests/StreamKeyResolutionTests.cs:ResolveStreamKey_WithStringProperty_ReturnsValueAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/StreamKeyResolutionTests.cs:ResolveStreamKey_WithGuidProperty_ReturnsStringValueAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/StreamKeyResolutionTests.cs:ResolveStreamKey_WithNoStreamKeyAttribute_ThrowsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/StreamKeyResolutionTests.cs:ResolveStreamKey_WithNullValue_ThrowsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/StreamKeyResolutionTests.cs:ResolveStreamKey_WithEmptyString_ThrowsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/StreamKeyResolutionTests.cs:ResolveStreamKey_WithWhitespaceString_ThrowsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/StreamKeyResolutionTests.cs:ResolveStreamKey_DifferentEventsForSameStream_ReturnsSameKeyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/StreamKeyResolutionTests.cs:ResolveStreamKey_WithConstructorParameter_ReturnsValueAsync</tests>
  public static string Resolve(IEvent @event) {
    // Delegate to source-generated zero-reflection implementation
    return StreamKeyExtractors.Resolve(@event);
  }
}
