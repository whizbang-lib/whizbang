namespace Whizbang.Core.Offloads;

/// <summary>
/// Sentinel payload that replaces a message's original body when the
/// send-side offload strategy decides the body is too large to send
/// inline. The receiver detects this payload, looks up the matching
/// <see cref="IMessageBodyStore"/>, downloads the body, and rehydrates
/// the original payload before invoking receptors.
/// </summary>
/// <param name="Claim">The body-store claim ticket — provider name, storage key, content hash.</param>
/// <param name="OriginalContentType">MIME type of the original (offloaded) payload, preserved so the receiver can deserialize correctly.</param>
/// <param name="OriginalTypeName">Assembly-qualified type name of the original payload, used by the receiver to route deserialization.</param>
/// <docs>offloads</docs>
public sealed record BodyClaimEnvelopePayload(
  MessageBodyClaim Claim,
  string OriginalContentType,
  string OriginalTypeName
);
