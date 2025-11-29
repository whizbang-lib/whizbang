using System.Text.Json.Serialization;

namespace Whizbang.Data.EFCore.Postgres.Serialization;

/// <summary>
/// DTO for serializing MessageEnvelope metadata to JSONB columns.
/// Used by EFCoreInbox, EFCoreOutbox, and EFCoreEventStore.
/// </summary>
public sealed class EnvelopeMetadataDto {
  [JsonPropertyName("correlationId")]
  public string? CorrelationId { get; set; }

  [JsonPropertyName("causationId")]
  public string? CausationId { get; set; }

  [JsonPropertyName("timestamp")]
  public DateTimeOffset Timestamp { get; set; }

  [JsonPropertyName("hops")]
  public required List<HopMetadataDto> Hops { get; set; }
}

/// <summary>
/// DTO for serializing MessageHop metadata.
/// </summary>
public sealed class HopMetadataDto {
  [JsonPropertyName("type")]
  public required string Type { get; set; }

  [JsonPropertyName("topic")]
  public required string Topic { get; set; }

  [JsonPropertyName("streamKey")]
  public required string StreamKey { get; set; }

  [JsonPropertyName("partitionIndex")]
  public int? PartitionIndex { get; set; }

  [JsonPropertyName("sequenceNumber")]
  public long? SequenceNumber { get; set; }

  [JsonPropertyName("securityContext")]
  public SecurityContextDto? SecurityContext { get; set; }

  [JsonPropertyName("metadata")]
  public IReadOnlyDictionary<string, object>? Metadata { get; set; }

  [JsonPropertyName("callerMemberName")]
  public string? CallerMemberName { get; set; }

  [JsonPropertyName("callerFilePath")]
  public string? CallerFilePath { get; set; }

  [JsonPropertyName("callerLineNumber")]
  public int? CallerLineNumber { get; set; }

  [JsonPropertyName("timestamp")]
  public DateTimeOffset Timestamp { get; set; }

  [JsonPropertyName("duration")]
  public TimeSpan? Duration { get; set; }
}

/// <summary>
/// DTO for serializing SecurityContext.
/// </summary>
public sealed class SecurityContextDto {
  [JsonPropertyName("userId")]
  public string? UserId { get; set; }

  [JsonPropertyName("tenantId")]
  public string? TenantId { get; set; }
}

/// <summary>
/// DTO for serializing security scope to JSONB columns.
/// </summary>
public sealed class ScopeDto {
  [JsonPropertyName("userId")]
  public string? UserId { get; set; }

  [JsonPropertyName("tenantId")]
  public string? TenantId { get; set; }
}
