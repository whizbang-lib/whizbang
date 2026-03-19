using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Policies;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Generated;

/// <summary>
/// Manual JsonSerializerContext for Whizbang infrastructure types.
/// This provides AOT-compatible JSON serialization for types used by MessageEnvelope
/// and other core Whizbang infrastructure.
/// NOTE: MessageId and CorrelationId have custom converters registered via WhizbangJsonContextInitializer.
/// Including them here enables custom MessageHopConverter to resolve them via GetTypeInfo().
/// NOTE: Nullable reference types (T?) cannot be used with typeof() - only nullable value types work.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Generated/InfrastructureJsonContextTests.cs:InfrastructureJsonContext_SerializesMessageHop_Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/InfrastructureJsonContextTests.cs:InfrastructureJsonContext_SerializesEnvelopeMetadata_Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/InfrastructureJsonContextTests.cs:InfrastructureJsonContext_SerializesServiceInstanceInfo_Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/InfrastructureJsonContextTests.cs:InfrastructureJsonContext_IgnoresNullPropertiesWhenSerializing_Async</tests>
// Value object types (with custom converters registered via WhizbangJsonContextInitializer)
[JsonSerializable(typeof(MessageId))]
[JsonSerializable(typeof(CorrelationId))]
[JsonSerializable(typeof(MessageHop))]
[JsonSerializable(typeof(List<MessageHop>))]
[JsonSerializable(typeof(EnvelopeMetadata))]
[JsonSerializable(typeof(ServiceInstanceInfo))]
[JsonSerializable(typeof(ServiceInstanceMetadata))]
[JsonSerializable(typeof(InboxMessageData))]
[JsonSerializable(typeof(OutboxMessageData))]
[JsonSerializable(typeof(MessageScope))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, System.Text.Json.JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, System.Text.Json.JsonElement?>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, System.Text.Json.JsonElement>))]
[JsonSerializable(typeof(SecurityContext))]
// ScopeDelta types for unified scope propagation
[JsonSerializable(typeof(ScopeDelta))]
[JsonSerializable(typeof(CollectionChanges))]
[JsonSerializable(typeof(ScopeProp))]
[JsonSerializable(typeof(Dictionary<ScopeProp, System.Text.Json.JsonElement>))]
[JsonSerializable(typeof(Dictionary<ScopeProp, CollectionChanges>))]
[JsonSerializable(typeof(ScopeContext))]
[JsonSerializable(typeof(PolicyDecisionTrail))]
[JsonSerializable(typeof(List<PolicyDecision>))]
[JsonSerializable(typeof(PolicyDecision))]
// Non-nullable primitive types for AOT compatibility
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
// Nullable primitive types for AOT compatibility (value types only)
[JsonSerializable(typeof(decimal?))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(long?))]
[JsonSerializable(typeof(bool?))]
[JsonSerializable(typeof(DateTime?))]
[JsonSerializable(typeof(DateTimeOffset?))]
[JsonSerializable(typeof(TimeSpan))]
[JsonSerializable(typeof(TimeSpan?))]
[JsonSerializable(typeof(DateOnly))]
[JsonSerializable(typeof(DateOnly?))]
[JsonSerializable(typeof(TimeOnly))]
[JsonSerializable(typeof(TimeOnly?))]
[JsonSerializable(typeof(Guid?))]
[JsonSerializable(typeof(double?))]
[JsonSerializable(typeof(float?))]
[JsonSerializable(typeof(byte?))]
[JsonSerializable(typeof(sbyte?))]
[JsonSerializable(typeof(short?))]
[JsonSerializable(typeof(ushort?))]
[JsonSerializable(typeof(uint?))]
[JsonSerializable(typeof(ulong?))]
[JsonSerializable(typeof(char?))]
// JsonElement support for outbox deserialization
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
[JsonSerializable(typeof(MessageEnvelope<System.Text.Json.JsonElement>))]
// Interface-based envelope types for polymorphic event store reads
// These are created by ReadPolymorphicAsync methods that return MessageEnvelope<IEvent>
[JsonSerializable(typeof(MessageEnvelope<IEvent>))]
[JsonSerializable(typeof(MessageEnvelope<ICommand>))]
[JsonSerializable(typeof(MessageEnvelope<IMessage>))]
[JsonSerializable(typeof(MessageEnvelope<object>))]
// Work coordinator types
[JsonSerializable(typeof(OutboxMessage))]
[JsonSerializable(typeof(OutboxMessage[]))]
[JsonSerializable(typeof(InboxMessage))]
[JsonSerializable(typeof(InboxMessage[]))]
[JsonSerializable(typeof(MessageCompletion))]
[JsonSerializable(typeof(MessageCompletion[]))]
[JsonSerializable(typeof(MessageFailure))]
[JsonSerializable(typeof(MessageFailure[]))]
[JsonSerializable(typeof(ReceptorProcessingCompletion))]
[JsonSerializable(typeof(ReceptorProcessingCompletion[]))]
[JsonSerializable(typeof(ReceptorProcessingFailure))]
[JsonSerializable(typeof(ReceptorProcessingFailure[]))]
[JsonSerializable(typeof(PerspectiveCursorCompletion))]
[JsonSerializable(typeof(PerspectiveCursorCompletion[]))]
[JsonSerializable(typeof(PerspectiveCursorFailure))]
[JsonSerializable(typeof(PerspectiveCursorFailure[]))]
[JsonSerializable(typeof(PerspectiveEventCompletion))]
[JsonSerializable(typeof(PerspectiveEventCompletion[]))]
[JsonSerializable(typeof(Guid[]))]
[JsonSerializable(typeof(Guid?[]))]  // Array of nullable Guids
// Sync inquiry types (for perspective sync awaiter)
[JsonSerializable(typeof(SyncInquiry))]
[JsonSerializable(typeof(SyncInquiry[]))]
[JsonSerializable(typeof(SyncInquiryResult))]
[JsonSerializable(typeof(SyncInquiryResult[]))]
// Perspective types
[JsonSerializable(typeof(PerspectiveMetadata))]
[JsonSerializable(typeof(PerspectiveScope))]
// Security principal types (for AllowedPrincipals in PerspectiveScope)
// SecurityPrincipalId is a readonly record struct (value type)
[JsonSerializable(typeof(SecurityPrincipalId))]
[JsonSerializable(typeof(SecurityPrincipalId?))]
[JsonSerializable(typeof(SecurityPrincipalId?[]))]
[JsonSerializable(typeof(List<SecurityPrincipalId>))]
[JsonSerializable(typeof(List<SecurityPrincipalId?>))]
[JsonSerializable(typeof(IReadOnlyList<SecurityPrincipalId>))]
// Core message interfaces (for polymorphic collections)
[JsonSerializable(typeof(IMessage))]
[JsonSerializable(typeof(IMessage[]))]
[JsonSerializable(typeof(List<IMessage>))]
[JsonSerializable(typeof(IEvent))]
[JsonSerializable(typeof(IEvent[]))]
[JsonSerializable(typeof(List<IEvent>))]
[JsonSerializable(typeof(ICommand))]
[JsonSerializable(typeof(ICommand[]))]
[JsonSerializable(typeof(List<ICommand>))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class InfrastructureJsonContext : JsonSerializerContext {
}
