using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Offloads;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// The body cipher configured from settings alone. An operator provides a cipher name, a key id and
/// a base64 32-byte key encryption key under <c>Whizbang:BodyOffload</c>; the host registers the
/// built-in AES-256-GCM cipher by that name and names it on the offload options, so every offloaded
/// body is sealed without a line of consumer code. A rotation window carries a previous key so bodies
/// sealed before the rotation still open. Misconfiguration fails at startup, naming the setting: a
/// cipher that silently did not engage would store plaintext while every dashboard read "sealed".
/// </summary>
/// <docs>fundamentals/offloads/message-body-store#cipher-from-settings</docs>
[Category("Unit")]
[Category("Offloads")]
public class BodyCipherFromConfigurationTests {
  private static readonly byte[] _keyA = RandomNumberGenerator.GetBytes(32);
  private static readonly byte[] _keyB = RandomNumberGenerator.GetBytes(32);

  private static IConfiguration _config(Dictionary<string, string?> values) =>
    new ConfigurationBuilder().AddInMemoryCollection(values).Build();

  private static ServiceProvider _build(Dictionary<string, string?> values) {
    var services = new ServiceCollection();
    services.AddWhizbangBodyCipherFromConfiguration(_config(values));
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task WithoutACipherName_RegistersNoCipher_AndLeavesTheOptionsAloneAsync() {
    using var sp = _build(new() {
      ["Whizbang:BodyOffload:ProviderName"] = "blob-prod",
    });

    await Assert.That(sp.GetKeyedService<IMessageBodyCipher>("body-aes-v1")).IsNull()
      .Because("no cipher name means bodies are stored as serialized, exactly as before");
    await Assert.That(sp.GetService<IOptions<MessageBodyOffloadOptions>>()?.Value.CipherName).IsNull();
  }

  [Test]
  public async Task WithANameAndAKey_RegistersTheAesGcmCipherByName_AndNamesItOnTheOptionsAsync() {
    using var sp = _build(new() {
      ["Whizbang:BodyOffload:CipherName"] = "body-aes-v1",
      ["Whizbang:BodyOffload:Cipher:KeyId"] = "kek-2026-09",
      ["Whizbang:BodyOffload:Cipher:KeyEncryptionKey"] = Convert.ToBase64String(_keyA),
    });

    var cipher = sp.GetRequiredKeyedService<IMessageBodyCipher>("body-aes-v1");
    var sealedBody = await cipher.SealAsync(new byte[] { 1, 2, 3, 4 });
    var opened = await cipher.OpenAsync(sealedBody.Bytes, sealedBody.Descriptor);

    await Assert.That(opened.ToArray()).IsEquivalentTo(new byte[] { 1, 2, 3, 4 });
    await Assert.That(sealedBody.Descriptor.CipherName).IsEqualTo("body-aes-v1");
    await Assert.That(sealedBody.Descriptor.KeyId).IsEqualTo("kek-2026-09")
      .Because("the key id from settings is what the claim records; it is a rotation label, never the key");
    await Assert.That(sp.GetRequiredService<IOptions<MessageBodyOffloadOptions>>().Value.CipherName).IsEqualTo("body-aes-v1")
      .Because("naming the cipher on the options is what makes the send side seal; registering it alone would not");
  }

  [Test]
  public async Task WithANameButNoKey_ThrowsAtStartup_NamingTheSettingAsync() {
    var act = () => _build(new() {
      ["Whizbang:BodyOffload:CipherName"] = "body-aes-v1",
      ["Whizbang:BodyOffload:Cipher:KeyId"] = "kek-2026-09",
    });

    var ex = await Assert.That(act).Throws<InvalidOperationException>();
    await Assert.That(ex!.Message).Contains("Whizbang:BodyOffload:Cipher:KeyEncryptionKey")
      .Because("a cipher that was named but never keyed must not start; storing plaintext under a sealed label is the failure this exists to prevent");
  }

  [Test]
  public async Task WithANameAndAKeyButNoKeyId_ThrowsAtStartup_NamingTheSettingAsync() {
    var act = () => _build(new() {
      ["Whizbang:BodyOffload:CipherName"] = "body-aes-v1",
      ["Whizbang:BodyOffload:Cipher:KeyEncryptionKey"] = Convert.ToBase64String(_keyA),
    });

    var ex = await Assert.That(act).Throws<InvalidOperationException>();
    await Assert.That(ex!.Message).Contains("Whizbang:BodyOffload:Cipher:KeyId");
  }

  [Test]
  [Arguments("not base64!!")]
  [Arguments("c2hvcnQ=")]
  public async Task WithAKeyThatIsNotBase64OrNot32Bytes_ThrowsAtStartup_NamingTheSettingAsync(string badKey) {
    var act = () => _build(new() {
      ["Whizbang:BodyOffload:CipherName"] = "body-aes-v1",
      ["Whizbang:BodyOffload:Cipher:KeyId"] = "kek-2026-09",
      ["Whizbang:BodyOffload:Cipher:KeyEncryptionKey"] = badKey,
    });

    var ex = await Assert.That(act).Throws<InvalidOperationException>();
    await Assert.That(ex!.Message).Contains("Whizbang:BodyOffload:Cipher:KeyEncryptionKey");
    await Assert.That(ex.Message).Contains("32 bytes");
  }

  [Test]
  public async Task WithAPreviousKey_OpensABodySealedUnderThePreviousKeyId_AndSealsUnderTheCurrentAsync() {
    // Before the rotation: one key, one label.
    var before = new AesGcmEnvelopeCipher("body-aes-v1", new LocalAesKeyWrapper("kek-2026-08", _keyA));
    var sealedBefore = await before.SealAsync(new byte[] { 9, 8, 7 });

    // After the rotation: the new key is current, the old one stays available for bodies already sealed.
    using var sp = _build(new() {
      ["Whizbang:BodyOffload:CipherName"] = "body-aes-v1",
      ["Whizbang:BodyOffload:Cipher:KeyId"] = "kek-2026-09",
      ["Whizbang:BodyOffload:Cipher:KeyEncryptionKey"] = Convert.ToBase64String(_keyB),
      ["Whizbang:BodyOffload:Cipher:PreviousKeyId"] = "kek-2026-08",
      ["Whizbang:BodyOffload:Cipher:PreviousKeyEncryptionKey"] = Convert.ToBase64String(_keyA),
    });
    var cipher = sp.GetRequiredKeyedService<IMessageBodyCipher>("body-aes-v1");

    var opened = await cipher.OpenAsync(sealedBefore.Bytes, sealedBefore.Descriptor);
    var sealedAfter = await cipher.SealAsync(new byte[] { 1 });

    await Assert.That(opened.ToArray()).IsEquivalentTo(new byte[] { 9, 8, 7 })
      .Because("a body sealed before the rotation names the previous key id on its claim and must still open during the window");
    await Assert.That(sealedAfter.Descriptor.KeyId).IsEqualTo("kek-2026-09")
      .Because("new bodies seal under the current key only; the previous key is for opening");
  }

  [Test]
  public async Task WithAPreviousKeyId_ButNoPreviousKey_ThrowsAtStartupAsync() {
    var act = () => _build(new() {
      ["Whizbang:BodyOffload:CipherName"] = "body-aes-v1",
      ["Whizbang:BodyOffload:Cipher:KeyId"] = "kek-2026-09",
      ["Whizbang:BodyOffload:Cipher:KeyEncryptionKey"] = Convert.ToBase64String(_keyB),
      ["Whizbang:BodyOffload:Cipher:PreviousKeyId"] = "kek-2026-08",
    });

    var ex = await Assert.That(act).Throws<InvalidOperationException>();
    await Assert.That(ex!.Message).Contains("Whizbang:BodyOffload:Cipher:PreviousKeyEncryptionKey")
      .Because("half a rotation window is a misconfiguration, not a smaller window");
  }

  [Test]
  public async Task WithAPreviousKey_ButNoPreviousKeyId_ThrowsAtStartupAsync() {
    var act = () => _build(new() {
      ["Whizbang:BodyOffload:CipherName"] = "body-aes-v1",
      ["Whizbang:BodyOffload:Cipher:KeyId"] = "kek-2026-09",
      ["Whizbang:BodyOffload:Cipher:KeyEncryptionKey"] = Convert.ToBase64String(_keyB),
      ["Whizbang:BodyOffload:Cipher:PreviousKeyEncryptionKey"] = Convert.ToBase64String(_keyA),
    });

    var ex = await Assert.That(act).Throws<InvalidOperationException>();
    await Assert.That(ex!.Message).Contains("Whizbang:BodyOffload:Cipher:PreviousKeyId")
      .Because("a previous key without its label could never be selected by a claim's key id");
  }

  [Test]
  public async Task AfterTheWindow_ABodySealedUnderTheRetiredKey_FailsToOpenAsync() {
    var before = new AesGcmEnvelopeCipher("body-aes-v1", new LocalAesKeyWrapper("kek-2026-08", _keyA));
    var sealedBefore = await before.SealAsync(new byte[] { 9, 8, 7 });

    using var sp = _build(new() {
      ["Whizbang:BodyOffload:CipherName"] = "body-aes-v1",
      ["Whizbang:BodyOffload:Cipher:KeyId"] = "kek-2026-09",
      ["Whizbang:BodyOffload:Cipher:KeyEncryptionKey"] = Convert.ToBase64String(_keyB),
    });
    var cipher = sp.GetRequiredKeyedService<IMessageBodyCipher>("body-aes-v1");

    await Assert.That(async () => await cipher.OpenAsync(sealedBefore.Bytes, sealedBefore.Descriptor))
      .Throws<CryptographicException>()
      .Because("once the previous key is dropped from settings, bodies sealed under it are unreadable; the receiver dead-letters them as an integrity failure rather than guessing");
  }
}
