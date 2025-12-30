using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Service instance metadata for heartbeat tracking.
/// Replaces JsonDocument? Metadata in ServiceInstanceRecord.
/// </summary>
public sealed class ServiceInstanceMetadata {
  /// <summary>Service version.</summary>
  [JsonPropertyName("version")]
  public string? Version { get; init; }

  /// <summary>Deployment environment.</summary>
  [JsonPropertyName("environment")]
  public string? Environment { get; init; }

  /// <summary>Cloud region.</summary>
  [JsonPropertyName("region")]
  public string? Region { get; init; }

  /// <summary>Additional custom metadata.</summary>
  [JsonPropertyName("custom")]
  public Dictionary<string, string>? Custom { get; init; }
}
