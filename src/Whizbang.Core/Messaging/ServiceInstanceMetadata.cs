using System.Collections.Generic;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Service instance metadata for heartbeat tracking.
/// Replaces JsonDocument? Metadata in ServiceInstanceRecord.
/// </summary>
public sealed class ServiceInstanceMetadata {
  /// <summary>Service version.</summary>
  public string? Version { get; init; }

  /// <summary>Deployment environment.</summary>
  public string? Environment { get; init; }

  /// <summary>Cloud region.</summary>
  public string? Region { get; init; }

  /// <summary>Additional custom metadata.</summary>
  public Dictionary<string, string>? Custom { get; init; }
}
