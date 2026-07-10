namespace Whizbang.Core.Offloads;

/// <summary>
/// Thrown when the receive-side rehydrator cannot download an offloaded body from the registered
/// <see cref="IMessageBodyStore"/> — a transient store/network error, or a download that exceeds
/// <see cref="MessageBodyOffloadOptions.DownloadTimeout"/>.
/// </summary>
/// <remarks>
/// This is deliberately NOT a dead-letter result. A download failure is very likely transient (the
/// body exists in the store), so the rehydrator throws and lets the transport redeliver the message
/// for a bounded, automatic retry. Only after the transport's own max-delivery count is exhausted does
/// the message land in the DLQ. Genuinely-permanent failures — unknown provider, hash-integrity
/// mismatch, deserialization error — remain terminal <see cref="RehydrateResult.DeadLetter"/> results.
/// </remarks>
public sealed class BodyClaimDownloadException : Exception {
  /// <summary>Creates a <see cref="BodyClaimDownloadException"/> with no message.</summary>
  public BodyClaimDownloadException() { }

  /// <summary>Creates a <see cref="BodyClaimDownloadException"/> with the given message.</summary>
  public BodyClaimDownloadException(string message) : base(message) { }

  /// <summary>Creates a <see cref="BodyClaimDownloadException"/> with the given message, wrapping the underlying download failure.</summary>
  public BodyClaimDownloadException(string message, Exception innerException)
    : base(message, innerException) { }
}
