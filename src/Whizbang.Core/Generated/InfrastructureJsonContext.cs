using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.Security;

namespace Whizbang.Core.Generated;

/// <summary>
/// Manual JsonSerializerContext for Whizbang infrastructure types.
/// This provides AOT-compatible JSON serialization for types used by MessageEnvelope
/// and other core Whizbang infrastructure.
/// NOTE: MessageId and CorrelationId (including nullable versions) are provided by WhizbangIdJsonContext
/// with custom converters. WhizbangIdJsonContext is registered FIRST in the resolver chain.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Generated/InfrastructureJsonContextTests.cs:InfrastructureJsonContext_SerializesMessageHop_Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/InfrastructureJsonContextTests.cs:InfrastructureJsonContext_SerializesEnvelopeMetadata_Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/InfrastructureJsonContextTests.cs:InfrastructureJsonContext_SerializesServiceInstanceInfo_Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/InfrastructureJsonContextTests.cs:InfrastructureJsonContext_IgnoresNullPropertiesWhenSerializing_Async</tests>
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
[JsonSerializable(typeof(SecurityContext))]
[JsonSerializable(typeof(PolicyDecisionTrail))]
[JsonSerializable(typeof(List<PolicyDecision>))]
[JsonSerializable(typeof(PolicyDecision))]
// Nullable primitive types for AOT compatibility
[JsonSerializable(typeof(decimal?))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(long?))]
[JsonSerializable(typeof(bool?))]
[JsonSerializable(typeof(DateTime?))]
[JsonSerializable(typeof(DateTimeOffset?))]
[JsonSerializable(typeof(Guid?))]
[JsonSerializable(typeof(double?))]
[JsonSerializable(typeof(float?))]
// JsonElement support for outbox deserialization
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
[JsonSerializable(typeof(MessageEnvelope<System.Text.Json.JsonElement>))]
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
[JsonSerializable(typeof(PerspectiveCheckpointCompletion))]
[JsonSerializable(typeof(PerspectiveCheckpointCompletion[]))]
[JsonSerializable(typeof(PerspectiveCheckpointFailure))]
[JsonSerializable(typeof(PerspectiveCheckpointFailure[]))]
[JsonSerializable(typeof(Guid[]))]
// Perspective types
[JsonSerializable(typeof(PerspectiveMetadata))]
[JsonSerializable(typeof(PerspectiveScope))]
// Security principal types (for AllowedPrincipals in PerspectiveScope)
[JsonSerializable(typeof(SecurityPrincipalId))]
[JsonSerializable(typeof(List<SecurityPrincipalId>))]
[JsonSerializable(typeof(IReadOnlyList<SecurityPrincipalId>))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class InfrastructureJsonContext : JsonSerializerContext {
}
