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
/// A strict id that implements <c>_isGrandfathered</c> accepts the frozen values it names through every checked entry
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

  /// <summary>
  /// A value the hook does not name is still refused by every checked entry point: From, Parse, the explicit cast and
  /// the JSON reader in both profiles (#2517).
  /// </summary>
  [Test]
  public async Task NotGrandfathered_IsStillRefusedByEveryCheckedEntryPointAsync() {
    var other = Guid.Parse(OTHER_V4);

    await Assert.That(() => GrandfatheredTestId.From(other)).ThrowsExactly<ArgumentException>();
    await Assert.That(() => GrandfatheredTestId.From(TrackedGuid.FromExternal(other))).ThrowsExactly<ArgumentException>();
    await Assert.That(() => GrandfatheredTestId.Parse(OTHER_V4)).ThrowsExactly<ArgumentException>();
    await Assert.That(() => (GrandfatheredTestId)other).ThrowsExactly<ArgumentException>();
    var json = await Assert.That(() => _readJson<GrandfatheredTestId>(OTHER_V4, SerializationProfile.Default)).ThrowsExactly<ArgumentException>();
    await Assert.That(json!.Message).Contains("GrandfatheredTestId requires UUIDv7 but received version 4");
    await Assert.That(() => _readJson<GrandfatheredTestId>(OTHER_V4, SerializationProfile.Persistence)).ThrowsExactly<ArgumentException>();
  }

  /// <summary>
  /// The hook names <c>Guid.Empty</c>, and every checked entry point refuses it anyway: the hook is never asked about
  /// it (#2517).
  /// </summary>
  [Test]
  public async Task EmptyGuid_IsRefusedByEveryCheckedEntryPointWhateverTheHookSaysAsync() {
    var empty = Guid.Empty.ToString();

    await Assert.That(GrandfatheredTestId.NamesTheEmptyGuid).IsTrue().Because("the fixture's hook says yes to it");
    await Assert.That(() => GrandfatheredTestId.From(Guid.Empty)).ThrowsExactly<ArgumentException>();
    await Assert.That(() => GrandfatheredTestId.From(TrackedGuid.FromExternal(Guid.Empty))).ThrowsExactly<ArgumentException>();
    await Assert.That(() => GrandfatheredTestId.Parse(empty)).ThrowsExactly<ArgumentException>();
    await Assert.That(() => (GrandfatheredTestId)Guid.Empty).ThrowsExactly<ArgumentException>();
    await Assert.That(() => _readJson<GrandfatheredTestId>(empty, SerializationProfile.Default)).ThrowsExactly<ArgumentException>();
    await Assert.That(() => _readJson<GrandfatheredTestId>(empty, SerializationProfile.Persistence)).ThrowsExactly<ArgumentException>();
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

  /// <summary>
  /// An id that does not implement the hook pays nothing for it: the compiler removes the unimplemented partial method
  /// and its call, leaving the helper to answer no, so the id refuses a value another id grandfathers (#2517).
  /// </summary>
  [Test]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3011:Reflection should not be used to increase accessibility",
    Justification = "What is asserted is which private members the compiler emitted: the helper, and no unimplemented hook.")]
  public async Task Unimplemented_TheCompilerRemovesTheHook_AndTheIdRefusesAsync() {
    const System.Reflection.BindingFlags STATIC_PRIVATE =
      System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;

    await Assert.That(typeof(StrictVersionTestId).GetMethod("_grandfathers", STATIC_PRIVATE)).IsNotNull();
    await Assert.That(typeof(StrictVersionTestId).GetMethod("_isGrandfathered", STATIC_PRIVATE)).IsNull()
      .Because("an unimplemented void partial method is not emitted");
    await Assert.That(typeof(GrandfatheredTestId).GetMethod("_isGrandfathered", STATIC_PRIVATE)).IsNotNull();
    await Assert.That(() => StrictVersionTestId.From(GrandfatheredTestId.SeededV4)).ThrowsExactly<ArgumentException>();
  }
}
