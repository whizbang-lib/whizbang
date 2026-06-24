using System.Text.Json.Serialization;
using Whizbang.Core.Observability;

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
// Bare event type — required for the saga emitter's PublishAsync<T> path.
[JsonSerializable(typeof(SagaCompletionWatchdogTickEvent))]
// Strongly-typed envelope wrapper — required for the transport consumer side
// (Service Bus / RabbitMQ) which receives `MessageEnvelope<T>` JSON and resolves
// the concrete generic via JsonTypeInfo. Without this entry, every consumer of
// Whizbang.Sagas hits a `JsonTypeInfo metadata for type
// 'Whizbang.Core.Observability.MessageEnvelope`1[Whizbang.Sagas.SagaCompletionWatchdogTickEvent]'
// was not provided` failure at watchdog-tick receive time (production dev 2026-06-24).
[JsonSerializable(typeof(MessageEnvelope<SagaCompletionWatchdogTickEvent>))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class SagasJsonContext : JsonSerializerContext;
