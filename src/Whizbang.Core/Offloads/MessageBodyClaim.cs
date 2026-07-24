namespace Whizbang.Core.Offloads;

/// <summary>
/// A ticket the offload feature substitutes for a message body when the body
/// exceeds the configured size threshold (claim-check pattern). Producers
/// upload the body via an <see cref="IMessageBodyStore"/>, replace
/// <c>envelope.Payload</c> with this claim, and send the small envelope on
/// the wire. Receivers see the claim, look up the matching store by
/// <see cref="ProviderName"/>, download the body, verify
/// <see cref="ContentHash"/>, and rehydrate the original payload.
/// </summary>
/// <param name="ProviderName">Stable key the receiver uses to resolve the matching <see cref="IMessageBodyStore"/>. Sender and receiver MUST register the same provider name pointing at compatible underlying storage (same container/bucket).</param>
/// <param name="StorageKey">Provider-opaque locator for the uploaded body. Format varies by provider (e.g., Azure Blob URL, S3 key, in-memory GUID).</param>
/// <param name="Size">Body size in bytes. Receiver uses this for pre-allocate and defensive caps (<see cref="MessageBodyDownloadOptions.MaxBytes"/>).</param>
/// <param name="ContentHash">SHA-256 (or provider-equivalent) of the body. Receiver MUST verify after download; mismatch → dead-letter with integrity-failure code.</param>
/// <param name="ContentType">MIME type of the original body so the receiver can deserialize into the correct payload type.</param>
/// <param name="UploadedAt">Wall-clock timestamp of upload. Useful for diagnostics and provider-side TTL bookkeeping; not part of the integrity check.</param>
/// <docs>offloads</docs>
public sealed record MessageBodyClaim(
  string ProviderName,
  string StorageKey,
  long Size,
  string ContentHash,
  string ContentType,
  DateTimeOffset UploadedAt
);
