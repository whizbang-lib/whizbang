using System.Security.Cryptography;
using System.Text;

namespace Whizbang.Core.Offloads;

/// <summary>
/// The built-in body cipher (issue #704): AES-256-GCM with envelope encryption. Every body gets a
/// fresh 32-byte data key and a fresh 12-byte nonce; the data key is wrapped by an
/// <see cref="IMessageBodyKeyWrapper"/> and travels on the claim in wrapped form only. The stored
/// bytes are ciphertext followed by the 16-byte authentication tag, so the body's integrity is
/// checked by the tag when it is opened, after the claim hash has already verified the stored
/// bytes. The cipher name and key identifier are bound into the tag as associated data, so a
/// sealed body cannot be replayed under a different cipher registration or key label.
/// </summary>
/// <remarks>
/// Allocation-light and trim-safe: <see cref="AesGcm"/> is a platform primitive, the descriptor is
/// a plain record, no reflection anywhere. Size overhead per body is the 16-byte tag.
/// </remarks>
/// <docs>fundamentals/offloads/message-body-store</docs>
/// <tests>tests/Whizbang.Core.Tests/Offloads/AesGcmEnvelopeCipherTests.cs</tests>
public sealed class AesGcmEnvelopeCipher : IMessageBodyCipher {
#pragma warning disable CA1707
  /// <summary>The algorithm identifier recorded on descriptors this cipher produces.</summary>
  public const string ALGORITHM = "AES-256-GCM";
#pragma warning restore CA1707

  private const int DATA_KEY_BYTES = 32;
  private const int NONCE_BYTES = 12;
  private const int TAG_BYTES = 16;
  private readonly IMessageBodyKeyWrapper _keyWrapper;

  /// <summary>Creates the cipher under a registered name with the wrapper that protects data keys.</summary>
  /// <param name="cipherName">The name sender and receiver register the cipher under.</param>
  /// <param name="keyWrapper">The key encryption key holder that wraps per-body data keys.</param>
  public AesGcmEnvelopeCipher(string cipherName, IMessageBodyKeyWrapper keyWrapper) {
    ArgumentException.ThrowIfNullOrWhiteSpace(cipherName);
    ArgumentNullException.ThrowIfNull(keyWrapper);
    CipherName = cipherName;
    _keyWrapper = keyWrapper;
  }

  /// <inheritdoc />
  public string CipherName { get; }

  /// <inheritdoc />
  public async ValueTask<SealedBody> SealAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default) {
    var dataKey = new byte[DATA_KEY_BYTES];
    RandomNumberGenerator.Fill(dataKey);
    var nonce = new byte[NONCE_BYTES];
    RandomNumberGenerator.Fill(nonce);
    var keyId = _keyWrapper.KeyId;
    var wrappedKey = await _keyWrapper.WrapAsync(dataKey, cancellationToken).ConfigureAwait(false);

    var sealedBytes = new byte[body.Length + TAG_BYTES];
    using (var aes = new AesGcm(dataKey, TAG_BYTES)) {
      aes.Encrypt(nonce, body.Span, sealedBytes.AsSpan(0, body.Length), sealedBytes.AsSpan(body.Length, TAG_BYTES), _associatedData(keyId));
    }
    CryptographicOperations.ZeroMemory(dataKey);

    return new SealedBody(sealedBytes, new MessageBodyCipherDescriptor(CipherName, ALGORITHM, keyId, nonce, wrappedKey));
  }

  /// <inheritdoc />
  public async ValueTask<ReadOnlyMemory<byte>> OpenAsync(ReadOnlyMemory<byte> sealedBody, MessageBodyCipherDescriptor descriptor, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(descriptor);
    if (!string.Equals(descriptor.Algorithm, ALGORITHM, StringComparison.Ordinal)) {
      throw new CryptographicException($"Descriptor algorithm '{descriptor.Algorithm}' is not {ALGORITHM}.");
    }
    if (descriptor.Nonce.Length != NONCE_BYTES) {
      throw new CryptographicException($"Descriptor nonce must be {NONCE_BYTES} bytes; got {descriptor.Nonce.Length}.");
    }
    if (sealedBody.Length < TAG_BYTES) {
      throw new CryptographicException("The sealed body is too short to carry an authentication tag.");
    }
    var dataKey = (await _keyWrapper.UnwrapAsync(descriptor.WrappedKey, descriptor.KeyId, cancellationToken).ConfigureAwait(false)).ToArray();
    try {
      var plaintextLength = sealedBody.Length - TAG_BYTES;
      var plaintext = new byte[plaintextLength];
      using var aes = new AesGcm(dataKey, TAG_BYTES);
      aes.Decrypt(descriptor.Nonce.Span, sealedBody.Span[..plaintextLength], sealedBody.Span[plaintextLength..], plaintext, _associatedData(descriptor.KeyId));
      return plaintext;
    } finally {
      CryptographicOperations.ZeroMemory(dataKey);
    }
  }

  private byte[] _associatedData(string keyId) => Encoding.UTF8.GetBytes($"whizbang.body:{CipherName}:{keyId}");
}
