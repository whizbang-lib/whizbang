namespace Whizbang.Core.Offloads;

/// <summary>
/// Optional per-call knobs for <see cref="IMessageBodyStore.UploadAsync"/>.
/// Providers honor what they understand and ignore (or document rejection of)
/// the rest — the goal is to expose provider-specific features (Azure Blob
/// access tier, custom metadata, container override, per-blob TTL, etc.)
/// without bloating the core contract.
/// </summary>
/// <remarks>
/// The send-side offload strategy populates these from envelope headers
/// (e.g., <c>whizbang.offload.tier</c>) plus offload config, so producers
/// don't construct them by hand at every call site.
/// </remarks>
/// <docs>fundamentals/offloads/message-body-store</docs>
public sealed record MessageBodyUploadOptions {
  /// <summary>
  /// Caller-supplied metadata key/value pairs (e.g., <c>correlation_id</c>,
  /// <c>source_service</c>). Persisted alongside the body where the provider
  /// supports it (Azure Blob: blob metadata; S3: object metadata).
  /// </summary>
  public IReadOnlyDictionary<string, string>? Metadata { get; init; }

  /// <summary>
  /// Provider-side TTL override; <c>null</c> falls back to the provider's
  /// configured default (e.g., a lifecycle rule on the storage container).
  /// </summary>
  public TimeSpan? Ttl { get; init; }

  /// <summary>
  /// Optional container/bucket/prefix override (e.g., route oversized DLQ
  /// payloads to a dedicated container). Providers that don't support
  /// per-call routing ignore this.
  /// </summary>
  public string? ContainerOverride { get; init; }

  /// <summary>
  /// Provider-specific opaque hints — e.g.,
  /// <c>"azure-blob.access_tier" = "Cool"</c>. Catch-all for things the
  /// typed options don't cover; providers document the keys they recognize.
  /// </summary>
  public IReadOnlyDictionary<string, object?>? ProviderHints { get; init; }
}
