namespace Whizbang.Core.Offloads;

/// <summary>
/// Configuration for the send-side body-offload (claim-check) strategy.
/// Determines when a message body is uploaded to a body-store and replaced
/// on the wire with a <see cref="MessageBodyClaim"/>.
/// </summary>
/// <docs>offloads</docs>
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
}
