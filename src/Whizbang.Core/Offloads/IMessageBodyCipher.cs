namespace Whizbang.Core.Offloads;

/// <summary>
/// Seals a message body before it leaves the process for an <see cref="IMessageBodyStore"/> and
/// opens it again on the receiver (issue #704). The store is pluggable, provider-backed and
/// long-lived, frequently a different trust domain from the broker, and the bodies that reach it
/// are by definition the largest messages a system produces. With a cipher configured the upload
/// call receives sealed bytes: the store cannot receive plaintext, because sealing happens inside
/// the offload path before the upload is made, not in a hook slot that ordering may or may not
/// place first.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MessageBodyClaim.ContentHash"/> stays a hash of the STORED bytes, so the receiver
/// verifies integrity before it decrypts rather than feeding unverified input to a cipher; an
/// authenticated cipher then covers the plaintext. Two checks, in the safe order, and the existing
/// dead-letter path is unchanged.
/// </para>
/// <para>
/// The cipher is resolved by <see cref="CipherName"/> on both sides, the same way the body store is
/// resolved by provider name: register it with
/// <c>services.AddWhizbangMessageBodyCipher&lt;T&gt;(name)</c> (or the built-in
/// <c>AddWhizbangAesGcmBodyCipher</c>) and name it in
/// <see cref="MessageBodyOffloadOptions.CipherName"/>. A claim without a
/// <see cref="MessageBodyClaim.Cipher"/> descriptor downloads exactly as before, so existing
/// bodies and stores are unaffected.
/// </para>
/// </remarks>
/// <docs>fundamentals/offloads/message-body-store</docs>
/// <tests>tests/Whizbang.Core.Tests/Offloads/BodyOffloadCipherTests.cs</tests>
public interface IMessageBodyCipher {
  /// <summary>Recorded on the claim so the receiver resolves the same cipher.</summary>
  string CipherName { get; }

  /// <summary>Seals a body: returns the bytes to store and the descriptor to carry on the claim.</summary>
  /// <param name="body">The plaintext body.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  ValueTask<SealedBody> SealAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default);

  /// <summary>
  /// Opens a sealed body using the descriptor the sender recorded on the claim. Throws
  /// <see cref="System.Security.Cryptography.CryptographicException"/> when the bytes do not
  /// authenticate under the described key (the receiver dead-letters as an integrity failure).
  /// </summary>
  /// <param name="sealedBody">The bytes as downloaded from the store, already hash-verified.</param>
  /// <param name="descriptor">The descriptor from the claim.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  ValueTask<ReadOnlyMemory<byte>> OpenAsync(ReadOnlyMemory<byte> sealedBody, MessageBodyCipherDescriptor descriptor, CancellationToken cancellationToken = default);
}

/// <summary>The output of <see cref="IMessageBodyCipher.SealAsync"/>: what to store, and what to put on the claim.</summary>
/// <param name="Bytes">The sealed bytes the store receives; the claim's content hash covers these.</param>
/// <param name="Descriptor">What the receiver needs to open the body. Never a key.</param>
/// <docs>fundamentals/offloads/message-body-store</docs>
public sealed record SealedBody(ReadOnlyMemory<byte> Bytes, MessageBodyCipherDescriptor Descriptor);

/// <summary>
/// What travels on the claim for a sealed body. Never a key: the data key is wrapped by a key
/// encryption key the store never holds (envelope encryption), so an operator with read access to
/// the container, a snapshot, or the account key sees ciphertext and a wrapped key they cannot
/// unwrap. Destroying or rotating the key encryption key makes every body under it unreadable
/// whether or not the blob was ever deleted.
/// </summary>
/// <param name="CipherName">The registered cipher the receiver resolves.</param>
/// <param name="Algorithm">The algorithm identifier, for example <c>AES-256-GCM</c>.</param>
/// <param name="KeyId">The key encryption key's identifier (a vault key URI, a rotation label), not the key.</param>
/// <param name="Nonce">The per-body nonce.</param>
/// <param name="WrappedKey">The per-body data key, wrapped by the key encryption key; empty when the cipher does not use envelope encryption.</param>
/// <docs>fundamentals/offloads/message-body-store</docs>
public sealed record MessageBodyCipherDescriptor(
  string CipherName,
  string Algorithm,
  string KeyId,
  ReadOnlyMemory<byte> Nonce,
  ReadOnlyMemory<byte> WrappedKey
);
