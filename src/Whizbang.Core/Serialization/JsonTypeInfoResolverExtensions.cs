using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.Core.Serialization;

/// <summary>
/// Extension methods for IJsonTypeInfoResolver to simplify combining user contexts with Whizbang contexts.
/// NOTE: With the JsonContextRegistry pattern, these methods are typically not needed.
/// Instead, register your contexts via ModuleInitializer and use JsonContextRegistry.CreateCombinedOptions().
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Serialization/JsonTypeInfoResolverExtensionsTests.cs</tests>
[Obsolete("Use JsonContextRegistry.RegisterContext() with ModuleInitializer pattern instead. " +
          "See Whizbang documentation for the recommended pattern.")]
public static class JsonTypeInfoResolverExtensions {
  /// <summary>
  /// Combines a user's JsonTypeInfoResolver with all registered Whizbang contexts.
  /// DEPRECATED: Use JsonContextRegistry.RegisterContext() with ModuleInitializer instead.
  /// </summary>
  /// <param name="userResolver">User's JsonSerializerContext or custom IJsonTypeInfoResolver</param>
  /// <returns>Combined resolver with all registered contexts plus user resolver</returns>
  /// <tests>tests/Whizbang.Core.Tests/Serialization/JsonTypeInfoResolverExtensionsTests.cs:CombineWithWhizbangContext_WithUserResolver_CombinesCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Serialization/JsonTypeInfoResolverExtensionsTests.cs:CombineWithWhizbangContext_WithNullResolver_ThrowsArgumentNullExceptionAsync</tests>
  [Obsolete("Use JsonContextRegistry.RegisterContext() with ModuleInitializer pattern instead.")]
  public static IJsonTypeInfoResolver CombineWithWhizbangContext(
    this IJsonTypeInfoResolver userResolver) {

    ArgumentNullException.ThrowIfNull(userResolver);

    // Get combined options from registry, then add user resolver
    var options = JsonContextRegistry.CreateCombinedOptions();
    var whizbangResolver = options.TypeInfoResolver
      ?? throw new InvalidOperationException("JsonContextRegistry has no registered contexts");

    return JsonTypeInfoResolver.Combine(
      whizbangResolver,
      userResolver
    );
  }

  /// <summary>
  /// Combines multiple user JsonTypeInfoResolvers with all registered Whizbang contexts.
  /// DEPRECATED: Use JsonContextRegistry.RegisterContext() with ModuleInitializer instead.
  /// </summary>
  /// <param name="userResolvers">One or more user contexts/resolvers</param>
  /// <returns>Combined resolver with all registered contexts plus user resolvers</returns>
  /// <tests>tests/Whizbang.Core.Tests/Serialization/JsonTypeInfoResolverExtensionsTests.cs:CombineWithWhizbangContext_WithMultipleResolvers_CombinesCorrectlyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Serialization/JsonTypeInfoResolverExtensionsTests.cs:CombineWithWhizbangContext_WithNullResolversArray_ThrowsArgumentNullExceptionAsync</tests>
  [Obsolete("Use JsonContextRegistry.RegisterContext() with ModuleInitializer pattern instead.")]
  public static IJsonTypeInfoResolver CombineWithWhizbangContext(
    params IJsonTypeInfoResolver[] userResolvers) {

    ArgumentNullException.ThrowIfNull(userResolvers);

    // Get combined options from registry
    var options = JsonContextRegistry.CreateCombinedOptions();
    var whizbangResolver = options.TypeInfoResolver
      ?? throw new InvalidOperationException("JsonContextRegistry has no registered contexts");

    var resolvers = new List<IJsonTypeInfoResolver> { whizbangResolver };
    resolvers.AddRange(userResolvers);

    return JsonTypeInfoResolver.Combine(resolvers.ToArray());
  }
}
