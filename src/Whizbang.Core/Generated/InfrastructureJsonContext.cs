using System.Collections.Generic;
using System.Text.Json.Serialization;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Generated;

/// <summary>
/// Manual JsonSerializerContext for Whizbang infrastructure types.
/// This provides AOT-compatible JSON serialization for types used by MessageEnvelope
/// and other core Whizbang infrastructure.
/// </summary>
[JsonSerializable(typeof(MessageHop))]
[JsonSerializable(typeof(List<MessageHop>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(SecurityContext))]
[JsonSerializable(typeof(PolicyDecisionTrail))]
[JsonSerializable(typeof(List<PolicyDecision>))]
[JsonSerializable(typeof(PolicyDecision))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class InfrastructureJsonContext : JsonSerializerContext {
}
