using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Whizbang.Core.Lenses;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Source-generated JsonSerializerContext for test types.
/// Registers test perspective models for POCO JSON mapping with Npgsql.
/// </summary>
[JsonSourceGenerationOptions(
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  WriteIndented = false)]
[JsonSerializable(typeof(Order))]
[JsonSerializable(typeof(SampleOrderCreatedEvent))]
[JsonSerializable(typeof(TestOrderId))]
[JsonSerializable(typeof(PerspectiveMetadata))]
[JsonSerializable(typeof(PerspectiveScope))]
[JsonSerializable(typeof(ActionTestModel))]
[JsonSerializable(typeof(ActionTestCreatedEvent))]
[JsonSerializable(typeof(ActionTestUpdatedEvent))]
[JsonSerializable(typeof(ActionTestSoftDeletedEvent))]
[JsonSerializable(typeof(ActionTestPurgedEvent))]
public partial class TestJsonContext : JsonSerializerContext {
  /// <summary>
  /// Module initializer that registers TestJsonContext with the global registry.
  /// Runs automatically when the assembly is loaded.
  /// </summary>
  // CA2255: Intentional use of ModuleInitializer in test code for JSON context registration
#pragma warning disable CA2255
  [ModuleInitializer]
#pragma warning restore CA2255
  internal static void Initialize() {
    JsonContextRegistry.RegisterContext(TestJsonContext.Default);
  }
}
