using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Serialization;

/// <summary>
/// Source-generated JsonSerializerContext for EF Core serialization.
/// Provides AOT-compatible JSON serialization for EnvelopeMetadata.
/// Note: MessageId, MessageHop, and related types are already handled by WhizbangJsonContext.
/// </summary>
[JsonSourceGenerationOptions(
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  WriteIndented = false)]
[JsonSerializable(typeof(EnvelopeMetadata))]
public partial class EFCoreJsonContext : JsonSerializerContext {
  /// <summary>
  /// Module initializer that registers EFCoreJsonContext with the global registry.
  /// Runs automatically when the assembly is loaded.
  /// </summary>
  [ModuleInitializer]
  internal static void Initialize() {
    JsonContextRegistry.RegisterContext(EFCoreJsonContext.Default);
  }

  /// <summary>
  /// Creates JsonSerializerOptions from the global JsonContextRegistry.
  /// Includes all registered contexts: Core (MessageHop, MessageId), EFCore, and application types.
  /// </summary>
  public static JsonSerializerOptions CreateCombinedOptions() {
    return JsonContextRegistry.CreateCombinedOptions();
  }
}
