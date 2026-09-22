using System.Security.Cryptography;

namespace Whizbang.Core.Offloads;

/// <summary>
/// Wraps and unwraps per-body data keys with a key encryption key the body store never holds
/// (envelope encryption). The built-in <see cref="AesGcmEnvelopeCipher"/> mints a fresh data key
/// per body, seals the body with it, and carries the data key on the claim only in wrapped form.
/// Implement this over a vault or a cloud key service (the key never leaves it) or use
/// <see cref="LocalAesKeyWrapper"/> with a key you supply.
/// </summary>
/// <docs>fundamentals/offloads/message-body-store</docs>
/// <tests>tests/Whizbang.Core.Tests/Offloads/AesGcmEnvelopeCipherTests.cs</tests>
public interface IMessageBodyKeyWrapper {
  /// <summary>Identifies the key encryption key on the claim; a vault key URI or a rotation label, never the key.</summary>
  string KeyId { get; }

  /// <summary>Wraps a data key under the current key encryption key.</summary>
  /// <param name="dataKey">The per-body data key.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  ValueTask<ReadOnlyMemory<byte>> WrapAsync(ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken = default);

  /// <summary>
  /// Unwraps a data key wrapped under the key encryption key identified by <paramref name="keyId"/>.
  /// Throws <see cref="CryptographicException"/> when the key is unknown or the wrapped key does not
  /// authenticate.
  /// </summary>
  /// <param name="wrappedDataKey">The wrapped data key from the claim.</param>
  /// <param name="keyId">The key identifier from the claim.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  ValueTask<ReadOnlyMemory<byte>> UnwrapAsync(ReadOnlyMemory<byte> wrappedDataKey, string keyId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A key wrapper over a key encryption key held in process: 32 bytes the host supplies (from its
/// secret store, never from source). Data keys are wrapped with AES-256-GCM under that key, so the
/// wrapped form authenticates and a wrong or rotated key fails to unwrap instead of yielding
/// garbage. Suitable for development and for hosts that manage their own key material; a vault
/// wrapper keeps the key out of the process entirely.
/// </summary>
/// <docs>fundamentals/offloads/message-body-store</docs>
public sealed class LocalAesKeyWrapper : IMessageBodyKeyWrapper {
  private const int KEY_BYTES = 32;
  private const int NONCE_BYTES = 12;
  private const int TAG_BYTES = 16;
  private readonly byte[] _keyEncryptionKey;

  /// <summary>Creates the wrapper over a 32-byte key encryption key.</summary>
  /// <param name="keyId">The identifier recorded on claims for this key.</param>
  /// <param name="keyEncryptionKey">The 32-byte key encryption key.</param>
  public LocalAesKeyWrapper(string keyId, ReadOnlyMemory<byte> keyEncryptionKey) {
    ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
    if (keyEncryptionKey.Length != KEY_BYTES) {
      throw new ArgumentException($"The key encryption key must be {KEY_BYTES} bytes (AES-256); got {keyEncryptionKey.Length}.", nameof(keyEncryptionKey));
    }
    KeyId = keyId;
    _keyEncryptionKey = keyEncryptionKey.ToArray();
  }

  /// <inheritdoc />
  public string KeyId { get; }

  /// <inheritdoc />
  public ValueTask<ReadOnlyMemory<byte>> WrapAsync(ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    var wrapped = new byte[NONCE_BYTES + dataKey.Length + TAG_BYTES];
    var nonce = wrapped.AsSpan(0, NONCE_BYTES);
    RandomNumberGenerator.Fill(nonce);
    using var aes = new AesGcm(_keyEncryptionKey, TAG_BYTES);
    aes.Encrypt(nonce, dataKey.Span, wrapped.AsSpan(NONCE_BYTES, dataKey.Length), wrapped.AsSpan(NONCE_BYTES + dataKey.Length, TAG_BYTES), _associatedData());
    return ValueTask.FromResult<ReadOnlyMemory<byte>>(wrapped);
  }

  /// <inheritdoc />
  public ValueTask<ReadOnlyMemory<byte>> UnwrapAsync(ReadOnlyMemory<byte> wrappedDataKey, string keyId, CancellationToken cancellationToken = default) {
    cancellationToken.ThrowIfCancellationRequested();
    if (!string.Equals(keyId, KeyId, StringComparison.Ordinal)) {
      throw new CryptographicException($"The claim was sealed under key '{keyId}', but this wrapper holds '{KeyId}'.");
    }
    if (wrappedDataKey.Length <= NONCE_BYTES + TAG_BYTES) {
      throw new CryptographicException("The wrapped data key is too short to carry a nonce, a key, and a tag.");
    }
    var span = wrappedDataKey.Span;
    var keyLength = span.Length - NONCE_BYTES - TAG_BYTES;
    var dataKey = new byte[keyLength];
    using var aes = new AesGcm(_keyEncryptionKey, TAG_BYTES);
    aes.Decrypt(span[..NONCE_BYTES], span.Slice(NONCE_BYTES, keyLength), span[(NONCE_BYTES + keyLength)..], dataKey, _associatedData());
    return ValueTask.FromResult<ReadOnlyMemory<byte>>(dataKey);
  }

  private byte[] _associatedData() => System.Text.Encoding.UTF8.GetBytes("whizbang.body-key:" + KeyId);
}
