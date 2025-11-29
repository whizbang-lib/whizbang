using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.Data.EFCore.Postgres.Serialization;

/// <summary>
/// Source-generated JsonSerializerContext for EF Core DTO serialization.
/// Provides AOT-compatible JSON serialization for metadata and scope DTOs.
/// </summary>
[JsonSourceGenerationOptions(
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  WriteIndented = false)]
[JsonSerializable(typeof(EnvelopeMetadataDto))]
[JsonSerializable(typeof(HopMetadataDto))]
[JsonSerializable(typeof(SecurityContextDto))]
[JsonSerializable(typeof(ScopeDto))]
[JsonSerializable(typeof(List<HopMetadataDto>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
public partial class EFCoreJsonContext : JsonSerializerContext {
  /// <summary>
  /// Creates JsonSerializerOptions that combines EFCore DTOs with Whizbang contexts.
  /// </summary>
  public static JsonSerializerOptions CreateCombinedOptions() {
    var resolvers = new List<IJsonTypeInfoResolver> {
      EFCoreJsonContext.Default,
      Whizbang.Core.Generated.WhizbangJsonContext.Default
    };

    return new JsonSerializerOptions {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(resolvers.ToArray()),
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
  }
}
