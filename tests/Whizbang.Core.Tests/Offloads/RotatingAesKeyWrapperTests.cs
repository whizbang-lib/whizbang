using System.Security.Cryptography;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Offloads;

namespace Whizbang.Core.Tests.Offloads;

/// <summary>
/// The rotation window: a wrapper that seals under the current key encryption key and opens under
/// either the current or the previous one, chosen by the key id the claim names.
/// </summary>
/// <docs>fundamentals/offloads/message-body-store#cipher-from-settings</docs>
[Category("Unit")]
[Category("Offloads")]
public class RotatingAesKeyWrapperTests {
  private static readonly byte[] _current = RandomNumberGenerator.GetBytes(32);
  private static readonly byte[] _previous = RandomNumberGenerator.GetBytes(32);

  [Test]
  public async Task KeyId_IsTheCurrentKeyId_AndWrap_UsesTheCurrentKeyAsync() {
    var wrapper = new RotatingAesKeyWrapper("kek-new", _current, "kek-old", _previous);
    var dataKey = RandomNumberGenerator.GetBytes(32);

    var wrapped = await wrapper.WrapAsync(dataKey);
    var currentOnly = new LocalAesKeyWrapper("kek-new", _current);
    var unwrapped = await currentOnly.UnwrapAsync(wrapped, "kek-new");

    await Assert.That(wrapper.KeyId).IsEqualTo("kek-new");
    await Assert.That(unwrapped.ToArray()).IsEquivalentTo(dataKey)
      .Because("what the rotating wrapper wraps, a plain wrapper over the current key alone can unwrap: new bodies never depend on the previous key");
  }

  [Test]
  public async Task Unwrap_UnderThePreviousKeyId_UsesThePreviousKeyAsync() {
    var previousOnly = new LocalAesKeyWrapper("kek-old", _previous);
    var dataKey = RandomNumberGenerator.GetBytes(32);
    var wrappedUnderOld = await previousOnly.WrapAsync(dataKey);
    var wrapper = new RotatingAesKeyWrapper("kek-new", _current, "kek-old", _previous);

    var unwrapped = await wrapper.UnwrapAsync(wrappedUnderOld, "kek-old");

    await Assert.That(unwrapped.ToArray()).IsEquivalentTo(dataKey);
  }

  [Test]
  public async Task Unwrap_UnderAnUnknownKeyId_ThrowsCryptographicAsync() {
    var wrapper = new RotatingAesKeyWrapper("kek-new", _current, "kek-old", _previous);
    var wrapped = await wrapper.WrapAsync(RandomNumberGenerator.GetBytes(32));

    await Assert.That(async () => await wrapper.UnwrapAsync(wrapped, "kek-retired"))
      .Throws<CryptographicException>()
      .Because("a key id outside the window is unknown; the caller dead-letters rather than guessing a key");
  }

  [Test]
  public async Task WithoutAPreviousKey_BehavesAsASingleKeyWrapperAsync() {
    var wrapper = new RotatingAesKeyWrapper("kek-new", _current, previousKeyId: null, previousKeyEncryptionKey: null);
    var wrapped = await wrapper.WrapAsync(RandomNumberGenerator.GetBytes(32));

    await Assert.That(wrapper.KeyId).IsEqualTo("kek-new");
    await Assert.That(async () => await wrapper.UnwrapAsync(wrapped, "kek-old")).Throws<CryptographicException>();
  }

  [Test]
  public async Task Constructor_RejectsHalfAWindow_AndTheSameIdForBothKeysAsync() {
    await Assert.That(() => new RotatingAesKeyWrapper("kek-new", _current, "kek-old", null)).Throws<ArgumentException>();
    await Assert.That(() => new RotatingAesKeyWrapper("kek-new", _current, null, _previous)).Throws<ArgumentException>();
    await Assert.That(() => new RotatingAesKeyWrapper("kek-new", _current, "kek-new", _previous)).Throws<ArgumentException>()
      .Because("two keys under one label would make the label ambiguous on every claim");
  }
}
