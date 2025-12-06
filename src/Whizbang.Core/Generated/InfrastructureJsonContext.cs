using System.Collections.Generic;
using System.Text.Json.Serialization;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Generated;

/// <summary>
/// Manual JsonSerializerContext for Whizbang infrastructure types.
/// This provides AOT-compatible JSON serialization for types used by MessageEnvelope
/// and other core Whizbang infrastructure.
/// NOTE: MessageId and CorrelationId (including nullable versions) are provided by WhizbangIdJsonContext
/// with custom converters. WhizbangIdJsonContext is registered FIRST in the resolver chain.
/// </summary>
[JsonSerializable(typeof(MessageHop))]
[JsonSerializable(typeof(List<MessageHop>))]
[JsonSerializable(typeof(ServiceInstanceInfo))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, System.Text.Json.JsonElement>))]
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
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class InfrastructureJsonContext : JsonSerializerContext {
}
