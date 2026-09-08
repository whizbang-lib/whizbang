#pragma warning disable CA1707

using System.Security.Cryptography;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Offloads;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// The built-in body cipher (issue #704): AES-256-GCM with a fresh data key and nonce per body,
/// the data key wrapped by the key encryption key and carried on the claim only in wrapped form.
/// The store sees ciphertext plus a tag; the descriptor never carries a key.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Offloads/AesGcmEnvelopeCipher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Offloads/IMessageBodyKeyWrapper.cs</code-under-test>
/// <docs>fundamentals/offloads/message-body-store</docs>
public class AesGcmEnvelopeCipherTests {
  private static readonly byte[] _kek = RandomNumberGenerator.GetBytes(32);

  private static AesGcmEnvelopeCipher _cipher(string keyId = "kek-1", byte[]? kek = null) =>
    new("kv-test", new LocalAesKeyWrapper(keyId, kek ?? _kek));

  [Test]
  public async Task Seal_ThenOpen_ReturnsTheOriginalBodyAsync() {
    var cipher = _cipher();
    var body = Encoding.UTF8.GetBytes("{\"caseId\":\"CASE-1\",\"claimant\":{\"name\":\"n\"}}");

    var sealedBody = await cipher.SealAsync(body);
    var opened = await cipher.OpenAsync(sealedBody.Bytes, sealedBody.Descriptor);

    await Assert.That(opened.ToArray()).IsEquivalentTo(body);
  }

  [Test]
  public async Task Seal_StoredBytesAreCiphertextPlusTag_AndCarryNoPlaintextAsync() {
    var cipher = _cipher();
    var body = Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog, twice, for good measure");

    var sealedBody = await cipher.SealAsync(body);

    await Assert.That(sealedBody.Bytes.Length).IsEqualTo(body.Length + 16)
      .Because("the overhead is exactly the GCM tag");
    await Assert.That(sealedBody.Bytes.Span.IndexOf("quick brown"u8)).IsEqualTo(-1)
      .Because("an operator reading the container must see nothing of the body");
  }

  [Test]
  public async Task Seal_Twice_UsesAFreshNonceAndDataKeyEachTimeAsync() {
    var cipher = _cipher();
    var body = "same body"u8.ToArray();

    var first = await cipher.SealAsync(body);
    var second = await cipher.SealAsync(body);

    await Assert.That(first.Descriptor.Nonce.ToArray()).IsNotEquivalentTo(second.Descriptor.Nonce.ToArray());
    await Assert.That(first.Descriptor.WrappedKey.ToArray()).IsNotEquivalentTo(second.Descriptor.WrappedKey.ToArray());
    await Assert.That(first.Bytes.ToArray()).IsNotEquivalentTo(second.Bytes.ToArray())
      .Because("two identical bodies must not produce identical ciphertext");
  }

  [Test]
  public async Task Descriptor_NamesTheCipherAlgorithmAndKey_AndNeverHoldsTheKeyAsync() {
    var cipher = _cipher(keyId: "https://vault.example/keys/body/9f2c");

    var sealedBody = await cipher.SealAsync("body"u8.ToArray());
    var descriptor = sealedBody.Descriptor;

    await Assert.That(descriptor.CipherName).IsEqualTo("kv-test");
    await Assert.That(descriptor.Algorithm).IsEqualTo(AesGcmEnvelopeCipher.ALGORITHM);
    await Assert.That(descriptor.KeyId).IsEqualTo("https://vault.example/keys/body/9f2c");
    await Assert.That(descriptor.Nonce.Length).IsEqualTo(12);
    await Assert.That(descriptor.WrappedKey.Length).IsEqualTo(12 + 32 + 16)
      .Because("the wrapped key is nonce + the 32-byte data key + tag under the key encryption key");
    await Assert.That(descriptor.WrappedKey.Span.IndexOf(_kek)).IsEqualTo(-1)
      .Because("the key encryption key never travels");
  }

  [Test]
  public async Task Open_TamperedCiphertext_ThrowsBeforeReturningAnythingAsync() {
    var cipher = _cipher();
    var sealedBody = await cipher.SealAsync("a body worth protecting"u8.ToArray());
    var tampered = sealedBody.Bytes.ToArray();
    tampered[3] ^= 0x01;

    await Assert.That(async () => await cipher.OpenAsync(tampered, sealedBody.Descriptor))
      .Throws<CryptographicException>()
      .Because("the tag authenticates the plaintext; one flipped bit fails to open");
  }

  [Test]
  public async Task Open_UnderADifferentKeyEncryptionKey_ThrowsAsync() {
    var sender = _cipher(keyId: "kek-1");
    var receiverWithWrongKey = _cipher(keyId: "kek-1", kek: RandomNumberGenerator.GetBytes(32));
    var sealedBody = await sender.SealAsync("body"u8.ToArray());

    await Assert.That(async () => await receiverWithWrongKey.OpenAsync(sealedBody.Bytes, sealedBody.Descriptor))
      .Throws<CryptographicException>()
      .Because("rotating or destroying the key encryption key makes every body under it unreadable, whether or not the blob was deleted");
  }

  [Test]
  public async Task Open_UnderAnUnknownKeyId_ThrowsAsync() {
    var sender = _cipher(keyId: "kek-1");
    var receiver = _cipher(keyId: "kek-2");
    var sealedBody = await sender.SealAsync("body"u8.ToArray());

    var ex = await Assert.That(async () => await receiver.OpenAsync(sealedBody.Bytes, sealedBody.Descriptor))
      .Throws<CryptographicException>();
    await Assert.That(ex!.Message).Contains("kek-1");
    await Assert.That(ex.Message).Contains("kek-2");
  }

  [Test]
  public async Task Open_UnderADifferentCipherName_ThrowsAsync() {
    // The cipher name is bound into the tag: a body sealed under one registration cannot be
    // opened by another even with the same key encryption key.
    var wrapper = new LocalAesKeyWrapper("kek-1", _kek);
    var sender = new AesGcmEnvelopeCipher("kv-prod", wrapper);
    var other = new AesGcmEnvelopeCipher("kv-archive", wrapper);
    var sealedBody = await sender.SealAsync("body"u8.ToArray());

    await Assert.That(async () => await other.OpenAsync(sealedBody.Bytes, sealedBody.Descriptor))
      .Throws<CryptographicException>();
  }

  [Test]
  public async Task Open_WrongAlgorithmOrShortInput_ThrowsCryptographicAsync() {
    var cipher = _cipher();
    var sealedBody = await cipher.SealAsync("body"u8.ToArray());

    await Assert.That(async () => await cipher.OpenAsync(sealedBody.Bytes, sealedBody.Descriptor with { Algorithm = "XCHACHA" }))
      .Throws<CryptographicException>();
    await Assert.That(async () => await cipher.OpenAsync(sealedBody.Bytes, sealedBody.Descriptor with { Nonce = new byte[4] }))
      .Throws<CryptographicException>();
    await Assert.That(async () => await cipher.OpenAsync(new byte[3], sealedBody.Descriptor))
      .Throws<CryptographicException>();
  }

  [Test]
  public async Task LocalAesKeyWrapper_RejectsAKeyThatIsNot32BytesAsync() {
    await Assert.That(() => new LocalAesKeyWrapper("k", new byte[16])).Throws<ArgumentException>();
    await Assert.That(() => new LocalAesKeyWrapper(" ", _kek)).Throws<ArgumentException>();
  }

  [Test]
  public async Task LocalAesKeyWrapper_TamperedWrappedKey_ThrowsAsync() {
    var wrapper = new LocalAesKeyWrapper("kek-1", _kek);
    var wrapped = (await wrapper.WrapAsync(RandomNumberGenerator.GetBytes(32))).ToArray();
    wrapped[^1] ^= 0x01;

    await Assert.That(async () => await wrapper.UnwrapAsync(wrapped, "kek-1")).Throws<CryptographicException>();
    await Assert.That(async () => await wrapper.UnwrapAsync(new byte[10], "kek-1")).Throws<CryptographicException>();
  }

  [Test]
  public async Task Constructor_RejectsMissingNameOrWrapperAsync() {
    await Assert.That(() => new AesGcmEnvelopeCipher("", new LocalAesKeyWrapper("k", _kek))).Throws<ArgumentException>();
    await Assert.That(() => new AesGcmEnvelopeCipher("kv", null!)).Throws<ArgumentNullException>();
  }
}
