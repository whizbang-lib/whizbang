namespace Whizbang.Core.Offloads;

/// <summary>
/// Optional per-call knobs for <see cref="IMessageBodyStore.DeleteAsync"/>.
/// </summary>
/// <docs>fundamentals/offloads/delete-options</docs>
public sealed record MessageBodyDeleteOptions {
  /// <summary>
  /// If <c>true</c>, treat "body not found" as success — idempotent delete.
  /// Defaults to <c>true</c> because the production cleanup path runs
  /// after PostInbox commit and a TTL backstop may have already removed
  /// the body; raising on missing-blob would be noise. Tests and forensic
  /// tools that want strict-mode delete set this to <c>false</c>.
  /// </summary>
  public bool IgnoreMissing { get; init; } = true;

  /// <summary>
  /// Provider-specific opaque hints. See
  /// <see cref="MessageBodyUploadOptions.ProviderHints"/>.
  /// </summary>
  public IReadOnlyDictionary<string, object?>? ProviderHints { get; init; }
}
