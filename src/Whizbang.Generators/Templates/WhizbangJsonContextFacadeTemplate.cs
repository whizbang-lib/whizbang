using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

#region NAMESPACE
namespace __NAMESPACE__;
#endregion

#region HEADER
// This region will be replaced with auto-generated header
#endregion

#nullable enable

/// <summary>
/// Generated JSON type resolver for __ASSEMBLY_NAME__.
/// Chains three contexts:
/// 1. WhizbangIdJsonContext (MessageId, CorrelationId from Whizbang.Core)
/// 2. MessageJsonContext (discovered messages in this assembly)
/// 3. InfrastructureJsonContext (MessageHop, SecurityContext, etc. from Whizbang.Core)
/// </summary>
public class WhizbangJsonContext : JsonSerializerContext, IJsonTypeInfoResolver {
  /// <summary>
  /// Default singleton instance of WhizbangJsonContext.
  /// Use this in JsonSerializerOptions: WhizbangJsonContext.Default
  /// </summary>
  public static WhizbangJsonContext Default { get; } = new();

  public WhizbangJsonContext() : base(null) { }
  public WhizbangJsonContext(JsonSerializerOptions options) : base(options) { }

  protected override JsonSerializerOptions? GeneratedSerializerOptions => null;

  /// <summary>
  /// Creates JsonSerializerOptions configured to use this resolver.
  /// Configured with WhenWritingNull ignore condition to match the settings in Core's InfrastructureJsonContext.
  /// </summary>
  public static JsonSerializerOptions CreateOptions() {
    // Combine local contexts (WhizbangIdJsonContext, MessageJsonContext)
    // with Core's InfrastructureJsonContext.
    // Note: WhizbangIdJsonContext and MessageJsonContext are generated PER ASSEMBLY.
    // WhizbangIdJsonContext is always in Whizbang.Core.Generated namespace.
    // MessageJsonContext is in the local {AssemblyName}.Generated namespace.
    var resolvers = new[] {
      (IJsonTypeInfoResolver)global::Whizbang.Core.Generated.WhizbangIdJsonContext.Default,
      (IJsonTypeInfoResolver)MessageJsonContext.Default,
      (IJsonTypeInfoResolver)global::Whizbang.Core.Generated.InfrastructureJsonContext.Default
    };

    return new JsonSerializerOptions {
      TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(resolvers),
      DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
  }

  /// <summary>
  /// Static combined resolver for local contexts and Core's InfrastructureJsonContext.
  /// This is used when GetTypeInfo is called directly on WhizbangJsonContext.
  /// </summary>
  private static readonly IJsonTypeInfoResolver _combinedResolver =
    System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(
      global::Whizbang.Core.Generated.WhizbangIdJsonContext.Default,
      MessageJsonContext.Default,
      global::Whizbang.Core.Generated.InfrastructureJsonContext.Default
    );

  /// <summary>
  /// Resolves type info by delegating to the combined resolver.
  /// Explicit interface implementation for IJsonTypeInfoResolver.
  /// </summary>
  JsonTypeInfo? IJsonTypeInfoResolver.GetTypeInfo(Type type, JsonSerializerOptions options) {
    return _combinedResolver.GetTypeInfo(type, options);
  }

  /// <summary>
  /// Resolves type info by delegating to the combined resolver.
  /// Override from JsonSerializerContext base class.
  /// </summary>
  public override JsonTypeInfo? GetTypeInfo(Type type) {
    // When called directly (not in resolver chain), Options might be null
    if (Options == null) return null;
    return _combinedResolver.GetTypeInfo(type, Options);
  }
}
