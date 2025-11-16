using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.Core.Serialization;

/// <summary>
/// Extension methods for IJsonTypeInfoResolver to simplify combining user contexts with WhizbangJsonContext.
/// </summary>
public static class JsonTypeInfoResolverExtensions {
  /// <summary>
  /// Combines a user's JsonTypeInfoResolver with WhizbangJsonContext.
  /// This is the recommended pattern for adding custom types while using Whizbang.
  /// </summary>
  /// <param name="userResolver">User's JsonSerializerContext or custom IJsonTypeInfoResolver</param>
  /// <returns>Combined resolver that checks Whizbang types first, then user types</returns>
  /// <example>
  /// <code>
  /// // User defines their context
  /// [JsonSerializable(typeof(MyCustomDto))]
  /// public partial class MyAppContext : JsonSerializerContext { }
  ///
  /// // Combine with Whizbang - beautiful one-liner!
  /// var options = new JsonSerializerOptions {
  ///     TypeInfoResolver = MyAppContext.Default.CombineWithWhizbangContext()
  /// };
  /// </code>
  /// </example>
  public static IJsonTypeInfoResolver CombineWithWhizbangContext(
    this IJsonTypeInfoResolver userResolver) {

    ArgumentNullException.ThrowIfNull(userResolver);

    return JsonTypeInfoResolver.Combine(
      Generated.WhizbangJsonContext.Default,
      userResolver
    );
  }

  /// <summary>
  /// Combines multiple user JsonTypeInfoResolvers with WhizbangJsonContext.
  /// Use this when you need to combine several user contexts.
  /// </summary>
  /// <param name="userResolvers">One or more user contexts/resolvers</param>
  /// <returns>Combined resolver with Whizbang types resolved first</returns>
  /// <example>
  /// <code>
  /// var resolver = JsonTypeInfoResolverExtensions.CombineWithWhizbangContext(
  ///     MyAppContext.Default,
  ///     ThirdPartyContext.Default
  /// );
  /// </code>
  /// </example>
  public static IJsonTypeInfoResolver CombineWithWhizbangContext(
    params IJsonTypeInfoResolver[] userResolvers) {

    ArgumentNullException.ThrowIfNull(userResolvers);

    var resolvers = new List<IJsonTypeInfoResolver> {
      Generated.WhizbangJsonContext.Default
    };
    resolvers.AddRange(userResolvers);

    return JsonTypeInfoResolver.Combine(resolvers.ToArray());
  }
}
