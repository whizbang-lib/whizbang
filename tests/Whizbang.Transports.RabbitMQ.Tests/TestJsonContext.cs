using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Test message type for integration tests.
/// </summary>
public sealed record TestMessage(string Content);

/// <summary>
/// Source-generated JSON context for test types.
/// Required for RabbitMQ transport deserialization via JsonContextRegistry.
/// </summary>
[JsonSerializable(typeof(TestMessage))]
[JsonSerializable(typeof(MessageEnvelope<TestMessage>))]
[JsonSourceGenerationOptions(
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
public partial class TestJsonContext : JsonSerializerContext;

/// <summary>
/// Module initializer to register test types with JsonContextRegistry.
/// Runs before Main() to ensure types are available for deserialization.
/// </summary>
internal static class TestJsonContextRegistration {
  [ModuleInitializer]
  internal static void Register() {
    JsonContextRegistry.RegisterContext(TestJsonContext.Default);

    // Register type name mappings for AOT-safe deserialization
    JsonContextRegistry.RegisterTypeName(
      typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!,
      typeof(MessageEnvelope<TestMessage>),
      TestJsonContext.Default
    );

    JsonContextRegistry.RegisterTypeName(
      typeof(TestMessage).AssemblyQualifiedName!,
      typeof(TestMessage),
      TestJsonContext.Default
    );
  }
}
