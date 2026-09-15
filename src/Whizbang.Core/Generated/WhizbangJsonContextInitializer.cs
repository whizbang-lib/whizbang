using System;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Generated;

/// <summary>
/// Auto-registration coordinator for Whizbang.Core's JsonSerializerContext instances.
/// Uses [ModuleInitializer] to register InfrastructureJsonContext, WhizbangIdJsonContext,
/// and MessageJsonContext with the global JsonContextRegistry.
/// </summary>
/// <remarks>
/// This class runs before Main() and registers Core's contexts FIRST, ensuring infrastructure
/// types (MessageHop, MessageId, CorrelationId) take precedence over application types.
/// No manual chaining required - JsonContextRegistry.CreateCombinedOptions() handles merging.
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangJsonContextTests.cs:Initialize_RegistersContextsWithRegistry_Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangJsonContextTests.cs:Initialize_RegistersConverters_Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangJsonContextTests.cs:Initialize_RunsBeforeMain_ViaModuleInitializerAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangJsonContextTests.cs:Initialize_RegistersLenientDateTimeOffsetConverters_Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangJsonContextTests.cs:CreateCombinedOptions_DeserializesPostgresInfinityTimestamp_Async</tests>
public static class WhizbangJsonContextInitializer {
  /// <summary>
  /// Module initializer that registers Whizbang.Core's JsonSerializerContext instances.
  /// Runs automatically when the assembly is loaded - no explicit call needed.
  /// Registers in priority order: WhizbangId → Infrastructure → Message.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangJsonContextTests.cs:Initialize_RegistersContextsWithRegistry_Async</tests>
  /// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangJsonContextTests.cs:Initialize_RegistersConverters_Async</tests>
  /// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangJsonContextTests.cs:Initialize_RunsBeforeMain_ViaModuleInitializerAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangJsonContextTests.cs:Initialize_RegistersLenientDateTimeOffsetConverters_Async</tests>
  /// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangJsonContextTests.cs:CreateCombinedOptions_DeserializesPostgresInfinityTimestamp_Async</tests>
  // CA2255: Intentional use of ModuleInitializer in library code for AOT-compatible JSON context registration
#pragma warning disable CA2255
  [ModuleInitializer]
#pragma warning restore CA2255
  public static void Initialize() {
    // Register Core contexts in priority order
    // WhizbangIdJsonContext FIRST to ensure custom converters for MessageId/CorrelationId take precedence
    // InfrastructureJsonContext SECOND for MessageHop, ServiceInstanceInfo (which use MessageId/CorrelationId)
    JsonContextRegistry.RegisterContext(WhizbangIdJsonContext.Default);
    JsonContextRegistry.RegisterContext(InfrastructureJsonContext.Default);
    JsonContextRegistry.RegisterContext(MessageJsonContext.Default);

    // Register WhizbangId converter instances from Whizbang.Core (no reflection - AOT compatible!)
    // This allows InfrastructureJsonContext to find them via TryGetTypeInfoForRuntimeCustomConverter
    JsonContextRegistry.RegisterConverter(new ValueObjects.MessageIdJsonConverter());
    JsonContextRegistry.RegisterConverter(new ValueObjects.CorrelationIdJsonConverter());

    // Register SecurityPrincipalId converter for string serialization.
    // This ensures AllowedPrincipals arrays serialize as ["user:alice", "group:sales"]
    // rather than [{"Value": "user:alice"}, {"Value": "group:sales"}].
    // Important for efficient JSONB array queries using PostgreSQL's containment operators.
    JsonContextRegistry.RegisterConverter(new Security.SecurityPrincipalIdJsonConverter());

    // Register TrackedGuid converter for UUID string serialization.
    // This ensures TrackedGuid values serialize as plain UUID strings like "019c7df5-494b-77d6-b994-e7145b796ec0"
    // rather than objects with Value/Metadata properties.
    // Important for PostgreSQL UUID column compatibility and JSONB queries.
    JsonContextRegistry.RegisterConverter(new ValueObjects.TrackedGuidJsonConverter());

    // Register lenient DateTimeOffset converters to handle PostgreSQL -infinity/infinity
    // literals that appear in JSONB columns when DateTimeOffset.MinValue/MaxValue are persisted.
    // Without these, manual JsonSerializer.Deserialize<T> calls on data::text JSONB reads throw
    // JsonException on -infinity strings.
    //
    // Scoped to the default (transport and event-store) profile. A perspective document is the
    // persistence profile's, where an offset is a number and the canonical converter below answers
    // for it, renderings included; the serializer takes the first converter that handles a type,
    // so the lenient one must not be on that profile at all.
    JsonContextRegistry.RegisterConverter(new LenientDateTimeOffsetConverter(), priority: 0, SerializationProfile.Default);
    JsonContextRegistry.RegisterConverter(new LenientNullableDateTimeOffsetConverter(), priority: 0, SerializationProfile.Default);

    // The canonical temporal form for every perspective document: a date, a time or a duration is
    // stored as microseconds so its extraction can carry an index. On the persistence profile's
    // options rather than per model, so the serializer applies them wherever the type occurs,
    // inherited, nested, in a collection element or in the framework's own metadata, which is the
    // same set Entity Framework's convention converts on the way back. Ahead of everything else,
    // so nothing profile-wide can answer for a temporal first.
    JsonContextRegistry.RegisterConverter(new Perspectives.CanonicalTemporalJsonConverters.InstantConverter(), priority: 100, SerializationProfile.Persistence);
    JsonContextRegistry.RegisterConverter(new Perspectives.CanonicalTemporalJsonConverters.OffsetInstantConverter(), priority: 100, SerializationProfile.Persistence);
    JsonContextRegistry.RegisterConverter(new Perspectives.CanonicalTemporalJsonConverters.DayConverter(), priority: 100, SerializationProfile.Persistence);
    JsonContextRegistry.RegisterConverter(new Perspectives.CanonicalTemporalJsonConverters.TimeOfDayConverter(), priority: 100, SerializationProfile.Persistence);
    JsonContextRegistry.RegisterConverter(new Perspectives.CanonicalTemporalJsonConverters.DurationConverter(), priority: 100, SerializationProfile.Persistence);

    // Register type name mappings for infrastructure types
    // This enables Azure Service Bus and other transports to deserialize messages by assembly-qualified name
    JsonContextRegistry.RegisterTypeName(
      "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      typeof(Observability.MessageEnvelope<System.Text.Json.JsonElement>),
      InfrastructureJsonContext.Default);
  }
}
