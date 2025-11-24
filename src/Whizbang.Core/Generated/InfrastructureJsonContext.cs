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
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, System.Text.Json.JsonElement>))]
[JsonSerializable(typeof(SecurityContext))]
[JsonSerializable(typeof(PolicyDecisionTrail))]
[JsonSerializable(typeof(List<PolicyDecision>))]
[JsonSerializable(typeof(PolicyDecision))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class InfrastructureJsonContext : JsonSerializerContext {
}
