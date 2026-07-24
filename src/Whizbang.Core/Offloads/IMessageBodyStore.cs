namespace Whizbang.Core.Offloads;

/// <summary>
/// A pluggable body store for the offload (claim-check) feature. When the
/// send-side offload strategy detects that a message body exceeds the
/// configured threshold, it calls <see cref="UploadAsync"/> on the active
/// store, substitutes the body with a <see cref="MessageBodyClaim"/>, and
/// sends the small envelope on the wire. The receive-side resolver looks
/// up the matching store by <see cref="ProviderName"/>, calls
/// <see cref="DownloadAsync"/>, verifies <see cref="MessageBodyClaim.ContentHash"/>,
/// and rehydrates the payload.
/// </summary>
/// <remarks>
/// <para>
/// Provider impls live in their own projects (parallel to the
/// <c>Whizbang.Transports.*</c> pattern) so consumers reference only the
/// providers they use. Built-in: <c>Whizbang.Offloads.InMemory</c> (dev/test),
/// <c>Whizbang.Offloads.AzureBlob</c> (production, Azurite + live).
/// </para>
/// <para>
/// All three operations accept an optional per-call options record. Providers
/// MUST tolerate <c>null</c> (no options → provider defaults) and MUST honor
/// the keys/fields they document; unknown <c>ProviderHints</c> are ignored.
/// </para>
/// </remarks>
/// <docs>offloads</docs>
public interface IMessageBodyStore {
  /// <summary>
  /// Stable identifier for this provider registration. Sender and receiver
  /// MUST register the same <see cref="ProviderName"/> pointing at compatible
  /// underlying storage (same Azure container, same S3 bucket); the receiver
  /// uses this name to resolve the matching store from DI when it sees a
  /// <see cref="MessageBodyClaim"/> on the wire.
  /// </summary>
  string ProviderName { get; }

  /// <summary>
  /// Uploads the body to provider storage and returns a claim ticket the
  /// receiver uses to download.
  /// </summary>
  /// <param name="body">The body bytes to upload. Provider may copy synchronously into its own buffer.</param>
  /// <param name="contentType">MIME type of the body. Preserved in the claim so the receiver can deserialize correctly.</param>
  /// <param name="options">Optional per-call overrides (metadata, TTL, container, provider hints). Null → provider defaults.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The claim ticket. <see cref="MessageBodyClaim.ProviderName"/> equals this store's <see cref="ProviderName"/>.</returns>
  Task<MessageBodyClaim> UploadAsync(
    ReadOnlyMemory<byte> body,
    string contentType,
    MessageBodyUploadOptions? options = null,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Downloads the body referenced by the claim. Caller verifies
  /// <see cref="MessageBodyClaim.ContentHash"/> against the returned bytes
  /// and dead-letters on mismatch.
  /// </summary>
  /// <param name="claim">The claim ticket, including <see cref="MessageBodyClaim.StorageKey"/>.</param>
  /// <param name="options">Optional per-call overrides (max-bytes cap, provider hints). Null → provider defaults.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The body bytes. Provider may return a view into its own buffer; caller MUST copy if it needs to outlive the call.</returns>
  Task<ReadOnlyMemory<byte>> DownloadAsync(
    MessageBodyClaim claim,
    MessageBodyDownloadOptions? options = null,
    CancellationToken cancellationToken = default);

  /// <summary>
  /// Deletes the body referenced by the claim. Provider behavior on
  /// not-found is governed by <see cref="MessageBodyDeleteOptions.IgnoreMissing"/>
  /// (default <c>true</c> — idempotent).
  /// </summary>
  /// <param name="claim">The claim ticket.</param>
  /// <param name="options">Optional per-call overrides. Null → provider defaults (idempotent).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  Task DeleteAsync(
    MessageBodyClaim claim,
    MessageBodyDeleteOptions? options = null,
    CancellationToken cancellationToken = default);
}
