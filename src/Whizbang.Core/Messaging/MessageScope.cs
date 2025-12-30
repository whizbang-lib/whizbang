namespace Whizbang.Core.Messaging;

/// <summary>
/// Multi-tenancy scope information for message processing.
/// Replaces JsonDocument? Scope in infrastructure entities.
/// </summary>
public sealed class MessageScope {
  /// <summary>Tenant identifier for multi-tenant isolation.</summary>
  public string? TenantId { get; init; }

  /// <summary>User identifier for user-level isolation.</summary>
  public string? UserId { get; init; }

  /// <summary>Partition key for stream partitioning.</summary>
  public string? PartitionKey { get; init; }
}
