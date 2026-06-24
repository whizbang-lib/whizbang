using System.Text.Json.Serialization;

namespace Whizbang.Sagas.Generated;

/// <summary>
/// AOT-safe <see cref="JsonSerializerContext"/> for the runtime event types
/// Whizbang.Sagas itself publishes via the framework's
/// <see cref="Services.ISagaEventEmitter"/>. Consumers don't reference these
/// types directly (their per-saga generated contracts cover all the
/// lifecycle events), so the per-consumer <c>MessageJsonContextGenerator</c>
/// never sees them — the framework MUST own its own context or every
/// consumer trips a <c>JsonTypeInfo metadata for type ... was not
/// provided</c> failure the first time
/// <c>BaseSagaService.InitiateSagaAsync</c> publishes the auto-armed
/// watchdog tick.
/// </summary>
/// <remarks>
/// Registered with <see cref="Core.Serialization.JsonContextRegistry"/>
/// at assembly load via <see cref="SagasJsonContextInitializer"/>'s
/// <c>[ModuleInitializer]</c>. Add new entries here whenever Whizbang.Sagas
/// gains a new framework-published runtime event type — never expect
/// consumers to register framework-internal types in their own contexts.
/// </remarks>
/// <docs>fundamentals/sagas/completion-orchestration</docs>
[JsonSerializable(typeof(SagaCompletionWatchdogTickEvent))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class SagasJsonContext : JsonSerializerContext;
