namespace Whizbang.Core.Offloads;

/// <summary>
/// Optional per-call knobs for <see cref="IMessageBodyStore.DownloadAsync"/>.
/// </summary>
/// <docs>fundamentals/offloads/message-body-store</docs>
public sealed record MessageBodyDownloadOptions {
  /// <summary>
  /// Defensive cap to refuse downloads above this size, regardless of what
  /// the claim reports. Defaults to the provider's configured cap; use this
  /// to tighten on a per-call basis (e.g., DLQ rehydrate path may want a
  /// smaller cap than the production path).
  /// </summary>
  public long? MaxBytes { get; init; }

  /// <summary>
  /// Provider-specific opaque hints. See
  /// <see cref="MessageBodyUploadOptions.ProviderHints"/>.
  /// </summary>
  public IReadOnlyDictionary<string, object?>? ProviderHints { get; init; }
}
