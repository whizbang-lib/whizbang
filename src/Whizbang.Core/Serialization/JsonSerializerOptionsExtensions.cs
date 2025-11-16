using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.Core.Serialization;

/// <summary>
/// Extension methods for JsonSerializerOptions to simplify Whizbang context configuration.
/// </summary>
public static class JsonSerializerOptionsExtensions {
  /// <summary>
  /// Creates JsonSerializerOptions configured with WhizbangJsonContext and optional user resolvers.
  /// This is the simplest one-liner for creating options with Whizbang + custom types.
  /// </summary>
  /// <param name="userResolvers">Optional user JsonSerializerContext instances or custom resolvers</param>
  /// <returns>Configured JsonSerializerOptions ready to use with JsonSerializer or JsonMessageSerializer</returns>
  /// <example>
  /// <code>
  /// // Simple case - just Whizbang types
  /// var options = JsonSerializerOptionsExtensions.CreateWithWhizbangContext();
  ///
  /// // With user context - one-liner!
  /// var options = JsonSerializerOptionsExtensions.CreateWithWhizbangContext(MyAppContext.Default);
  ///
  /// // Multiple contexts
  /// var options = JsonSerializerOptionsExtensions.CreateWithWhizbangContext(
  ///     MyAppContext.Default,
  ///     ThirdPartyContext.Default
  /// );
  /// </code>
  /// </example>
  public static JsonSerializerOptions CreateWithWhizbangContext(
    params IJsonTypeInfoResolver[] userResolvers) {

    var resolver = userResolvers.Length == 0
      ? Generated.WhizbangJsonContext.Default
      : JsonTypeInfoResolverExtensions.CombineWithWhizbangContext(userResolvers);

    return new JsonSerializerOptions {
      TypeInfoResolver = resolver
    };
  }
}
