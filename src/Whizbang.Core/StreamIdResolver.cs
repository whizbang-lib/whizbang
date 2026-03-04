using Whizbang.Core.Generated;

namespace Whizbang.Core;

/// <summary>
/// Resolves stream keys from events using [StreamId] attribute.
/// Uses source-generated code for zero-reflection AOT compatibility.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/StreamIdResolutionTests.cs</tests>
public static class StreamIdResolver {
  /// <summary>
  /// Resolves the stream key from an event.
  /// Looks for a property or parameter marked with [StreamId] attribute.
  /// </summary>
  /// <param name="event">The event to resolve the stream key from</param>
  /// <returns>The stream key as a string</returns>
  /// <exception cref="InvalidOperationException">
  /// Thrown when no [StreamId] attribute is found, or when the stream key value is null or empty
  /// </exception>
  /// <tests>tests/Whizbang.Core.Tests/StreamIdResolutionTests.cs:ResolveStreamId_WithStringProperty_ReturnsValueAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/StreamIdResolutionTests.cs:ResolveStreamId_WithGuidProperty_ReturnsStringValueAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/StreamIdResolutionTests.cs:ResolveStreamId_WithNoStreamIdAttribute_ThrowsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/StreamIdResolutionTests.cs:ResolveStreamId_WithNullValue_ThrowsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/StreamIdResolutionTests.cs:ResolveStreamId_WithEmptyString_ThrowsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/StreamIdResolutionTests.cs:ResolveStreamId_WithWhitespaceString_ThrowsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/StreamIdResolutionTests.cs:ResolveStreamId_DifferentEventsForSameStream_ReturnsSameKeyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/StreamIdResolutionTests.cs:ResolveStreamId_WithConstructorParameter_ReturnsValueAsync</tests>
  public static string Resolve(IEvent @event) {
    // Delegate to source-generated zero-reflection implementation
    return StreamIdExtractors.Resolve(@event);
  }
}
