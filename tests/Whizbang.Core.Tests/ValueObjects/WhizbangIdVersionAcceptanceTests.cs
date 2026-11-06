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
/// Which Guids a generated strong id takes. By default only UUIDv7; with
/// <c>[WhizbangId(AcceptAnyVersion = true)]</c>, any non-empty Guid, through every way in: <c>From</c>,
/// <c>Parse</c>, the explicit cast, JSON (default and persistence profiles), the constructor and the
/// <c>Value</c> initializer.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/WhizbangIdGenerator.cs</code-under-test>
[Category("ValueObjects")]
public class WhizbangIdVersionAcceptanceTests {
  private const string V4 = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
  private const string V5 = "886313e1-3b8a-5372-9b90-0c9aee199e5d";
  private const string V7 = "019e244a-6bda-78a9-a08f-a1011c9c31dd";
  private const string V8 = "a1b2c3d4-e5f6-8789-8abc-def012345678";

  private static T _readJson<T>(string guid, SerializationProfile profile) {
    var options = JsonContextRegistry.CreateCombinedOptions(profile);
    return (T)JsonSerializer.Deserialize($"\"{guid}\"", options.GetTypeInfo(typeof(T)))!;
  }

  private static string _writeJson<T>(T id, SerializationProfile profile) {
    var options = JsonContextRegistry.CreateCombinedOptions(profile);
    return JsonSerializer.Serialize(id, options.GetTypeInfo(typeof(T)));
  }

  // ========================================
  // AcceptAnyVersion = true
  // ========================================

  [Test]
  [Arguments(V4)]
  [Arguments(V5)]
  [Arguments(V7)]
  [Arguments(V8)]
  public async Task AnyVersion_FromGuid_RoundTripsAsync(string text) {
    var guid = Guid.Parse(text);

    var id = AnyVersionTestId.From(guid);

    await Assert.That(id.Value).IsEqualTo(guid);
    await Assert.That((Guid)id).IsEqualTo(guid);
  }

  [Test]
  [Arguments(V4)]
  [Arguments(V5)]
  [Arguments(V7)]
  [Arguments(V8)]
  public async Task AnyVersion_FromTrackedGuid_RoundTripsAsync(string text) {
    var guid = Guid.Parse(text);

    var id = AnyVersionTestId.From(TrackedGuid.FromExternal(guid));

    await Assert.That(id.Value).IsEqualTo(guid);
  }

  [Test]
  [Arguments(V4)]
  [Arguments(V5)]
  [Arguments(V7)]
  [Arguments(V8)]
  public async Task AnyVersion_ParseAndCast_RoundTripAsync(string text) {
    var guid = Guid.Parse(text);

    var parsed = AnyVersionTestId.Parse(text);
    var cast = (AnyVersionTestId)guid;

    await Assert.That(parsed.Value).IsEqualTo(guid);
    await Assert.That(parsed.ToString()).IsEqualTo(text);
    await Assert.That(cast.Value).IsEqualTo(guid);
  }

  [Test]
  [Arguments(V4)]
  [Arguments(V5)]
  [Arguments(V7)]
  [Arguments(V8)]
  public async Task AnyVersion_ConstructorAndValueInit_RoundTripAsync(string text) {
    var guid = Guid.Parse(text);

    var constructed = new AnyVersionTestId(guid);
    var initialized = new AnyVersionTestId { Value = guid };

    await Assert.That(constructed.Value).IsEqualTo(guid);
    await Assert.That(initialized.Value).IsEqualTo(guid);
  }

  [Test]
  [Arguments(V4, SerializationProfile.Default)]
  [Arguments(V5, SerializationProfile.Default)]
  [Arguments(V7, SerializationProfile.Default)]
  [Arguments(V8, SerializationProfile.Default)]
  [Arguments(V4, SerializationProfile.Persistence)]
  [Arguments(V5, SerializationProfile.Persistence)]
  [Arguments(V7, SerializationProfile.Persistence)]
  [Arguments(V8, SerializationProfile.Persistence)]
  public async Task AnyVersion_Json_RoundTripsAsync(string text, SerializationProfile profile) {
    var id = AnyVersionTestId.From(Guid.Parse(text));

    var json = _writeJson(id, profile);
    var read = _readJson<AnyVersionTestId>(text, profile);

    await Assert.That(json).IsEqualTo($"\"{text}\"");
    await Assert.That(read).IsEqualTo(id);
  }

  [Test]
  public async Task AnyVersion_New_StillProducesUuidV7Async() {
    var id = AnyVersionTestId.New();

    await Assert.That(id.Value.Version).IsEqualTo(7);
    await Assert.That(id.GetIsTracking()).IsTrue();
  }

  [Test]
  public async Task AnyVersion_EmptyGuid_IsRejectedEverywhereAsync() {
    var empty = Guid.Empty;

    await Assert.That(() => AnyVersionTestId.From(empty)).ThrowsExactly<ArgumentException>();
    await Assert.That(() => AnyVersionTestId.From(TrackedGuid.FromExternal(empty))).ThrowsExactly<ArgumentException>();
    await Assert.That(() => AnyVersionTestId.Parse(empty.ToString())).ThrowsExactly<ArgumentException>();
    await Assert.That(() => (AnyVersionTestId)empty).ThrowsExactly<ArgumentException>();
    await Assert.That(() => new AnyVersionTestId(empty)).ThrowsExactly<ArgumentException>();
    await Assert.That(() => new AnyVersionTestId { Value = empty }).ThrowsExactly<ArgumentException>();
    await Assert.That(() => _readJson<AnyVersionTestId>(empty.ToString(), SerializationProfile.Default))
      .Throws<Exception>();
  }

  [Test]
  public async Task AnyVersion_EmptyGuid_MessageNamesTheIdTypeAsync() {
    var thrown = Assert.Throws<ArgumentException>(() => AnyVersionTestId.From(Guid.Empty));

    await Assert.That(thrown.Message).Contains("AnyVersionTestId requires a non-empty Guid");
    await Assert.That(thrown.ParamName).IsEqualTo("value");
  }

  // ========================================
  // Default (UUIDv7 only)
  // ========================================

  [Test]
  [Arguments(V4)]
  [Arguments(V5)]
  [Arguments(V8)]
  public async Task Strict_NonV7_IsRejectedByFromParseCastAndJsonAsync(string text) {
    var guid = Guid.Parse(text);

    await Assert.That(() => StrictVersionTestId.From(guid)).ThrowsExactly<ArgumentException>();
    await Assert.That(() => StrictVersionTestId.From(TrackedGuid.FromExternal(guid))).ThrowsExactly<ArgumentException>();
    await Assert.That(() => StrictVersionTestId.Parse(text)).ThrowsExactly<ArgumentException>();
    await Assert.That(() => (StrictVersionTestId)guid).ThrowsExactly<ArgumentException>();
    await Assert.That(() => _readJson<StrictVersionTestId>(text, SerializationProfile.Default)).Throws<Exception>();
  }

  [Test]
  public async Task Strict_V7_RoundTripsAsync() {
    var guid = Guid.Parse(V7);

    await Assert.That(StrictVersionTestId.From(guid).Value).IsEqualTo(guid);
    await Assert.That(StrictVersionTestId.Parse(V7).Value).IsEqualTo(guid);
    await Assert.That(((StrictVersionTestId)guid).Value).IsEqualTo(guid);
    await Assert.That(_readJson<StrictVersionTestId>(V7, SerializationProfile.Default).Value).IsEqualTo(guid);
  }

  [Test]
  public async Task Strict_EmptyGuid_IsRejectedByFromAsync() {
    await Assert.That(() => StrictVersionTestId.From(Guid.Empty)).ThrowsExactly<ArgumentException>();
  }
}
