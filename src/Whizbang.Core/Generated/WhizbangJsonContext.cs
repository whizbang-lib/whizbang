using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.Core.Generated;

/// <summary>
/// Hand-written JSON type resolver that coordinates the three independently-generated Core contexts.
/// This acts as the aggregator for InfrastructureJsonContext, WhizbangIdJsonContext, and MessageJsonContext.
/// </summary>
/// <remarks>
/// This resolver chains through the three Core contexts in order, allowing them to be generated
/// independently without coordination. Compiled into Core.dll and referenced by consumer projects.
/// </remarks>
public class WhizbangJsonContext : JsonSerializerContext, IJsonTypeInfoResolver {
  /// <summary>
  /// Default singleton instance of WhizbangJsonContext.
  /// Use this in JsonSerializerOptions: WhizbangJsonContext.Default
  /// </summary>
  public static WhizbangJsonContext Default { get; } = new();

  /// <summary>
  /// Creates a new instance with no options (for use with Default singleton).
  /// </summary>
  public WhizbangJsonContext() : base(null) { }

  /// <summary>
  /// Creates a new instance with the specified options.
  /// </summary>
  public WhizbangJsonContext(JsonSerializerOptions options) : base(options) { }

  /// <summary>
  /// Gets the generated serializer options. Returns null as this is a resolver-only context.
  /// </summary>
  protected override JsonSerializerOptions? GeneratedSerializerOptions => null;

  /// <summary>
  /// Creates JsonSerializerOptions configured to use this resolver.
  /// Configured with camelCase property naming and WhenWritingNull ignore condition
  /// to match the settings in InfrastructureJsonContext.
  /// </summary>
  public static JsonSerializerOptions CreateOptions() {
    // Combine the three Core contexts into a single resolver chain
    var resolvers = new[] {
      (IJsonTypeInfoResolver)InfrastructureJsonContext.Default,
      (IJsonTypeInfoResolver)WhizbangIdJsonContext.Default,
      (IJsonTypeInfoResolver)MessageJsonContext.Default
    };

    return new JsonSerializerOptions {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(resolvers),
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
  }

  /// <summary>
  /// Static combined resolver for the three Core contexts.
  /// This is used when GetTypeInfo is called directly on WhizbangJsonContext.
  /// </summary>
  private static readonly IJsonTypeInfoResolver _combinedResolver = JsonTypeInfoResolver.Combine(
    InfrastructureJsonContext.Default,
    WhizbangIdJsonContext.Default,
    MessageJsonContext.Default
  );

  /// <summary>
  /// Resolves type info by delegating to the combined resolver.
  /// </summary>
  JsonTypeInfo? IJsonTypeInfoResolver.GetTypeInfo(Type type, JsonSerializerOptions options) {
    return _combinedResolver.GetTypeInfo(type, options);
  }

  /// <summary>
  /// Overrides base GetTypeInfo to use this instance's Options property.
  /// </summary>
  public override JsonTypeInfo? GetTypeInfo(Type type) {
    // When called directly (not in resolver chain), Options might be null
    if (Options == null) {
      return null;
    }

    return _combinedResolver.GetTypeInfo(type, Options);
  }
}
