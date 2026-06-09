namespace Whizbang.Core.Offloads;

/// <summary>
/// Decides whether a serialized message body should be sent inline or
/// uploaded to an <see cref="IMessageBodyStore"/> and replaced on the
/// wire with a <see cref="BodyClaimEnvelopePayload"/>. The send-side
/// half of the claim-check pattern.
/// </summary>
/// <remarks>
/// <para>
/// The default implementation (<see cref="MessageBodyOffloadStrategy"/>)
/// reads <see cref="MessageBodyOffloadOptions.SizeThresholdBytes"/>, the
/// transport's <c>MaxMessageSizeBytes</c>, and the active
/// <see cref="IMessageBodyStore"/> registration to decide. Custom
/// implementations can layer policy (per-destination thresholds,
/// always-offload-for-archive-tier, etc.).
/// </para>
/// <para>
/// The strategy operates on the <em>serialized body bytes</em>, not the
/// raw payload, so the size check is exact (post-serialization, no guesswork).
/// </para>
/// </remarks>
/// <docs>fundamentals/offloads/offload-strategy</docs>
public interface IMessageBodyOffloadStrategy {
  /// <summary>
  /// Inspects the serialized body and decides whether to offload. Returns
  /// the original body to send inline, or uploads + returns a sentinel
  /// indicating that the wire body should be a serialized
  /// <see cref="BodyClaimEnvelopePayload"/>.
  /// </summary>
  /// <param name="originalBody">Serialized payload bytes.</param>
  /// <param name="contentType">MIME type of the serialized payload.</param>
  /// <param name="originalTypeName">Assembly-qualified type name of the payload — preserved in the sentinel so the receiver can deserialize.</param>
  /// <param name="transportMaxMessageSizeBytes">Hard wire-ceiling from <c>ITransport.MaxMessageSizeBytes</c>; null = unlimited.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>An <see cref="OffloadDecision"/> describing whether the body was offloaded and what to send on the wire.</returns>
  Task<OffloadDecision> MaybeOffloadAsync(
    ReadOnlyMemory<byte> originalBody,
    string contentType,
    string originalTypeName,
    long? transportMaxMessageSizeBytes,
    CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of <see cref="IMessageBodyOffloadStrategy.MaybeOffloadAsync"/>.
/// Either the body is small enough to send inline (<see cref="Offloaded"/>
/// false), or it was uploaded and replaced with a claim sentinel.
/// </summary>
public sealed record OffloadDecision {
  /// <summary>True if the body was uploaded; false if it should be sent inline.</summary>
  public required bool Offloaded { get; init; }

  /// <summary>When <see cref="Offloaded"/> is true, the sentinel payload to substitute on the wire. Null when not offloaded.</summary>
  public BodyClaimEnvelopePayload? Sentinel { get; init; }

  /// <summary>Convenience builder for the "send inline" decision.</summary>
  public static OffloadDecision SendInline() => new() { Offloaded = false };

  /// <summary>Convenience builder for the "offloaded" decision.</summary>
  public static OffloadDecision Offload(BodyClaimEnvelopePayload sentinel)
    => new() { Offloaded = true, Sentinel = sentinel };
}
