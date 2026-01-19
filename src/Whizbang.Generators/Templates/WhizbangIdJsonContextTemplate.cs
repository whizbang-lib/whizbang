using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace __NAMESPACE__;

#region HEADER
// This region will be replaced with auto-generated header
#endregion

#nullable enable

/// <summary>
/// JsonTypeInfoResolver for discovered WhizbangId types in this assembly.
/// Provides custom converters for WhizbangId value objects with UUIDv7 serialization.
/// </summary>
public class WhizbangIdJsonContext : IJsonTypeInfoResolver {
  /// <summary>
  /// Default singleton instance of WhizbangIdJsonContext.
  /// </summary>
  public static WhizbangIdJsonContext Default { get; } = new();

  /// <summary>
  /// Resolves JsonTypeInfo for discovered WhizbangId types in this assembly.
  /// </summary>
  public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) {
    #region TYPE_CHECKS
    // This region will be replaced with generated type checks
    #endregion

    // Type not handled by this resolver
    return null;
  }
}
