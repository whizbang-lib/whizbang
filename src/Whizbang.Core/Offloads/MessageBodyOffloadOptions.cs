namespace Whizbang.Core.Offloads;

/// <summary>
/// Configuration for the send-side body-offload (claim-check) strategy.
/// Determines when a message body is uploaded to a body-store and replaced
/// on the wire with a <see cref="MessageBodyClaim"/>.
/// </summary>
/// <docs>fundamentals/offloads/message-body-store</docs>
public sealed class MessageBodyOffloadOptions {
  /// <summary>
  /// Provider name to use for offload. MUST match a registered
  /// <see cref="IMessageBodyStore"/> (via
  /// <see cref="OffloadServiceCollectionExtensions.AddWhizbangMessageBodyStore{TStore}"/>).
  /// <c>null</c> disables offload entirely — large messages publish inline
  /// regardless of transport size limits (and may hit transport ceilings).
  /// </summary>
  public string? ProviderName { get; set; }

  /// <summary>
  /// Body size in bytes at or above which offload kicks in. Set BELOW the
  /// transport's <c>MaxMessageSizeBytes</c> to leave headroom for envelope
  /// metadata (typical: 64 KB for Azure Service Bus Standard's 256 KB
  /// ceiling — 25% of the wire limit).
  /// </summary>
  public long SizeThresholdBytes { get; set; } = 64L * 1024L;

  /// <summary>
  /// When <c>true</c>, the PostInbox lifecycle hook explicitly deletes the
  /// body via <see cref="IMessageBodyStore.DeleteAsync"/> after the inbox
  /// row is fully acked. When <c>false</c> (default), rely on the
  /// provider's TTL / lifecycle rule to clean up — simpler and avoids
  /// cleanup races in fan-out subscriber topologies.
  /// </summary>
  public bool ActiveCleanup { get; set; }

  /// <summary>
  /// Age past which the passive sweep deletes an offloaded blob and removes its ledger row.
  /// Default 30 days; <c>null</c> turns the sweep off entirely (the provider-side lifecycle rule
  /// is then the ONLY cleanup — verify it exists). Evaluated against the ledger's
  /// <c>uploaded_at</c> (DB clock) AT SWEEP TIME, so changing this value is retroactive over every
  /// existing blob; nothing is stamped per blob.
  /// <para>
  /// FLOOR: must exceed the transport's DLQ retention plus expected recovery latency — a
  /// dead-lettered claim envelope re-driven through DLQ recovery must still rehydrate, or the
  /// replay dies with <see cref="BodyClaimDownloadException"/>.
  /// </para>
  /// </summary>
  public TimeSpan? PassiveExpiry { get; set; } = TimeSpan.FromDays(30);

  /// <summary>
  /// Minimum interval between passive sweeps service-wide. Replicas race on the settings CAS
  /// watermark and exactly one wins per window, so N replicas never issue N delete storms against
  /// the same container. Default 1 hour.
  /// </summary>
  public TimeSpan PassiveSweepClaimWindow { get; set; } = TimeSpan.FromHours(1);

  /// <summary>Ledger rows fetched per sweep batch. Default 500.</summary>
  public int PassiveSweepBatchSize { get; set; } = 500;

  /// <summary>
  /// Upper bound on batches per maintenance cycle, so an adoption-sized backlog drains across
  /// cycles instead of monopolizing one. Default 10.
  /// </summary>
  public int PassiveSweepMaxBatchesPerCycle { get; set; } = 10;

  /// <summary>
  /// Bounded timeout for a single body-store download during receive-side rehydration. A download
  /// that exceeds this is aborted and surfaced as a retryable failure (the transport redelivers the
  /// message) instead of stalling the consumer indefinitely on a hung blob call. Default: 100s.
  /// </summary>
  public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromSeconds(100);
}
