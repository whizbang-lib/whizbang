using System.Runtime.CompilerServices;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;

namespace Whizbang.Sagas.Generated;

/// <summary>
/// Auto-registration coordinator for Whizbang.Sagas's
/// <see cref="SagasJsonContext"/>. Mirrors the
/// <c>Whizbang.Core.Generated.WhizbangJsonContextInitializer</c> pattern so
/// the framework's own runtime event types (currently
/// <see cref="SagaCompletionWatchdogTickEvent"/>) are resolvable by the
/// cross-assembly <see cref="JsonContextRegistry"/> the moment Whizbang.Sagas
/// is loaded — without the consumer having to register framework-internal
/// types in their own context.
/// </summary>
/// <remarks>
/// Without this, every consumer of Whizbang.Sagas hits
/// <c>NotSupportedException: JsonTypeInfo metadata for type
/// 'Whizbang.Sagas.SagaCompletionWatchdogTickEvent' was not provided</c>
/// the first time <c>BaseSagaService.InitiateSagaAsync</c> publishes the
/// auto-armed watchdog tick (production dev 2026-06-24).
/// </remarks>
public static class SagasJsonContextInitializer {
  // CA2255: Intentional use of ModuleInitializer in library code for AOT-compatible JSON context registration.
#pragma warning disable CA2255
  [ModuleInitializer]
#pragma warning restore CA2255
  public static void Initialize() {
    JsonContextRegistry.RegisterContext(SagasJsonContext.Default);

    // Register the assembly-qualified type-name mapping for the strongly-typed
    // envelope. The transport consumer's _extractMessageTypeFromEnvelopeType +
    // GetTypeInfoByName path resolves wire-side envelope-type strings to their
    // concrete generic types; without this entry it falls back to reflection
    // (AOT-broken) and emits `Failed to resolve message type
    // 'Whizbang.Sagas.SagaCompletionWatchdogTickEvent, Whizbang.Sagas...'`.
    JsonContextRegistry.RegisterTypeName(
      "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Sagas.SagaCompletionWatchdogTickEvent, Whizbang.Sagas]], Whizbang.Core",
      typeof(MessageEnvelope<SagaCompletionWatchdogTickEvent>),
      SagasJsonContext.Default);
  }
}
