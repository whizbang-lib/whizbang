// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// A strict id that implements <c>IsGrandfathered</c> accepts the frozen values it names through every checked entry
/// point, and keeps refusing every other value that breaks its version rule (#2517).
/// </summary>
/// <code-under-test>src/Whizbang.Generators/WhizbangIdGenerator.cs</code-under-test>
[Category("ValueObjects")]
public class WhizbangIdGrandfatheredTests {
  private const string OTHER_V4 = "6ba7b810-9dad-41d1-80b4-00c04fd430c8";

  private static T _readJson<T>(string guid, SerializationProfile profile) {
    var options = JsonContextRegistry.CreateCombinedOptions(profile);
    return (T)JsonSerializer.Deserialize($"\"{guid}\"", options.GetTypeInfo(typeof(T)))!;
  }

  public static IEnumerable<Func<Guid>> Seeded() {
    yield return () => GrandfatheredTestId.SeededV4;
    yield return () => GrandfatheredTestId.SeededV8;
  }

  [Test]
  [MethodDataSource(nameof(Seeded))]
  public async Task Grandfathered_IsAcceptedByEveryCheckedEntryPointAsync(Guid seeded) {
    var text = seeded.ToString();

    await Assert.That(GrandfatheredTestId.From(seeded).Value).IsEqualTo(seeded);
    await Assert.That(GrandfatheredTestId.From(TrackedGuid.FromExternal(seeded)).Value).IsEqualTo(seeded);
    await Assert.That(GrandfatheredTestId.Parse(text).Value).IsEqualTo(seeded);
    await Assert.That(((GrandfatheredTestId)seeded).Value).IsEqualTo(seeded);
    await Assert.That(_readJson<GrandfatheredTestId>(text, SerializationProfile.Default).Value).IsEqualTo(seeded);
    await Assert.That(_readJson<GrandfatheredTestId>(text, SerializationProfile.Persistence).Value).IsEqualTo(seeded);
  }

  [Test]
  public async Task NotGrandfathered_IsStillRefusedAsync() {
    var other = Guid.Parse(OTHER_V4);

    await Assert.That(() => GrandfatheredTestId.From(other)).ThrowsExactly<ArgumentException>();
    await Assert.That(() => GrandfatheredTestId.From(TrackedGuid.FromExternal(other))).ThrowsExactly<ArgumentException>();
    await Assert.That(() => GrandfatheredTestId.Parse(OTHER_V4)).ThrowsExactly<ArgumentException>();
    await Assert.That(() => _readJson<GrandfatheredTestId>(OTHER_V4, SerializationProfile.Default)).Throws<Exception>();
  }

  /// <summary>The hook names <c>Guid.Empty</c>, and the id refuses it anyway.</summary>
  [Test]
  public async Task EmptyGuid_IsRefusedWhateverTheHookSaysAsync() {
    await Assert.That(() => GrandfatheredTestId.From(Guid.Empty)).ThrowsExactly<ArgumentException>();
    await Assert.That(() => GrandfatheredTestId.From(TrackedGuid.FromExternal(Guid.Empty)))
      .ThrowsExactly<ArgumentException>();
  }

  [Test]
  public async Task New_StillMintsUuidV7Async() {
    var id = GrandfatheredTestId.New();

    await Assert.That(id.Value.Version).IsEqualTo(7);
  }

  [Test]
  public async Task AllowList_GrandfatheredV4AndListedVersions_AreAcceptedAndOthersRefusedAsync() {
    var v5 = Guid.Parse("886313e1-3b8a-5372-9b90-0c9aee199e5d");

    await Assert.That(GrandfatheredAllowListTestId.From(GrandfatheredAllowListTestId.SeededV4).Value)
      .IsEqualTo(GrandfatheredAllowListTestId.SeededV4);
    await Assert.That(GrandfatheredAllowListTestId.From(v5).Value).IsEqualTo(v5);
    await Assert.That(() => GrandfatheredAllowListTestId.From(Guid.Parse(OTHER_V4))).ThrowsExactly<ArgumentException>();
  }

  /// <summary>An id that does not implement the hook grandfathers nothing.</summary>
  [Test]
  public async Task Unimplemented_GrandfathersNothingAsync() {
    await Assert.That(() => StrictVersionTestId.From(GrandfatheredTestId.SeededV4)).ThrowsExactly<ArgumentException>();
  }
}
