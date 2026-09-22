using System.Security.Cryptography;

namespace Whizbang.Core.Offloads;

/// <summary>
/// A key wrapper for the rotation window: seals under the current key encryption key and opens
/// under either the current or the previous one, chosen by the key id the claim names.
/// </summary>
/// <remarks>
/// <para>
/// The key id is bound into every sealed body, so a body sealed under one label opens only under
/// that label. A rotation therefore needs a window in which the new key seals and the old key still
/// opens what was sealed before. This wrapper composes two <see cref="LocalAesKeyWrapper"/>s: wrap
/// always goes to the current key, unwrap goes to whichever key the claim's id names, and an id
/// outside the window is a <see cref="CryptographicException"/> the receiver dead-letters as an
/// integrity failure rather than a guess.
/// </para>
/// <para>
/// Half a window (a previous id without a key, or the reverse) and a shared label for both keys are
/// rejected at construction: both would make a claim's key id ambiguous or unresolvable.
/// </para>
/// </remarks>
/// <docs>fundamentals/offloads/message-body-store#cipher-from-settings</docs>
/// <tests>tests/Whizbang.Core.Tests/Offloads/RotatingAesKeyWrapperTests.cs</tests>
public sealed class RotatingAesKeyWrapper : IMessageBodyKeyWrapper {
  private readonly LocalAesKeyWrapper _current;
  private readonly LocalAesKeyWrapper? _previous;

  /// <summary>
  /// Creates the wrapper. <paramref name="previousKeyId"/> and
  /// <paramref name="previousKeyEncryptionKey"/> are given together during a rotation window and
  /// omitted together outside one.
  /// </summary>
  /// <exception cref="ArgumentException">
  /// Half a window was given, or the previous label equals the current one.
  /// </exception>
  public RotatingAesKeyWrapper(
      string currentKeyId,
      ReadOnlyMemory<byte> currentKeyEncryptionKey,
      string? previousKeyId,
      ReadOnlyMemory<byte>? previousKeyEncryptionKey) {
    _current = new LocalAesKeyWrapper(currentKeyId, currentKeyEncryptionKey);
    var hasPreviousId = !string.IsNullOrWhiteSpace(previousKeyId);
    var hasPreviousKey = previousKeyEncryptionKey is not null;
    if (hasPreviousId != hasPreviousKey) {
      throw new ArgumentException(
        "A rotation window needs both the previous key id and the previous key encryption key, or neither.",
        hasPreviousId ? nameof(previousKeyEncryptionKey) : nameof(previousKeyId));
    }
    if (hasPreviousId) {
      if (string.Equals(previousKeyId, currentKeyId, StringComparison.Ordinal)) {
        throw new ArgumentException(
          $"The previous key id '{previousKeyId}' equals the current one; two keys under one label would make every claim's key id ambiguous.",
          nameof(previousKeyId));
      }
      _previous = new LocalAesKeyWrapper(previousKeyId!, previousKeyEncryptionKey!.Value);
    }
  }

  /// <inheritdoc />
  public string KeyId => _current.KeyId;

  /// <inheritdoc />
  public ValueTask<ReadOnlyMemory<byte>> WrapAsync(ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken = default)
    => _current.WrapAsync(dataKey, cancellationToken);

  /// <inheritdoc />
  public ValueTask<ReadOnlyMemory<byte>> UnwrapAsync(ReadOnlyMemory<byte> wrappedDataKey, string keyId, CancellationToken cancellationToken = default) {
    if (string.Equals(keyId, _current.KeyId, StringComparison.Ordinal)) {
      return _current.UnwrapAsync(wrappedDataKey, keyId, cancellationToken);
    }
    if (_previous is not null && string.Equals(keyId, _previous.KeyId, StringComparison.Ordinal)) {
      return _previous.UnwrapAsync(wrappedDataKey, keyId, cancellationToken);
    }
    throw new CryptographicException(
      _previous is null
        ? $"The claim was sealed under key '{keyId}', but this wrapper holds '{_current.KeyId}' and no previous key."
        : $"The claim was sealed under key '{keyId}', but this wrapper holds '{_current.KeyId}' and '{_previous.KeyId}'.");
  }
}
