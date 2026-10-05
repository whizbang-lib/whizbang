// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using FsCheck.Fluent;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Tests.Helpers;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Property tests for <see cref="TrackedGuid"/> and the id value objects built on it: what the metadata flags
/// promise for every combination of flags, and that the JSON converters round-trip every Guid in every culture.
/// </summary>
[Category("Core")]
[Category("ValueObjects")]
[Category("IdGeneration")]
[Category("Property")]
public class TrackedGuidPropertyTests {
  private const GuidMetadatas CREATION_SOURCES =
    GuidMetadatas.SourceWhizbang | GuidMetadatas.SourceMedo | GuidMetadatas.SourceMicrosoft;

  private static readonly string[] _cultureNames =
    [.. CultureInfo.GetCultures(CultureTypes.SpecificCultures).Select(c => c.Name).Prepend(CultureInfo.InvariantCulture.Name)];

  /// <summary>
  /// For every combination of metadata flags: the flags survive unchanged; sub-millisecond precision is only
  /// ever claimed by an id that is also tracking (so it is authoritative whenever it is true); tracking depends
  /// only on the three creation sources, so flags recording where an id was later seen (parsed, external,
  /// unknown, third-party) never make an inferred id look authoritative; and equality and hashing ignore the
  /// metadata entirely.
  /// </summary>
  [Test]
  public async Task Metadata_AnyFlagCombination_KeepsTrackingAndPrecisionConsistentAsync() {
    var flags = Gen.Choose(0, ushort.MaxValue).Select(f => (GuidMetadatas)f);
    var input = ArbMap.Default.GeneratorFor<Guid>().Zip(flags).Zip(flags).ToArbitrary();
    var property = Prop.ForAll(input, x => _metadataHolds(x.Item1.Item1, x.Item1.Item2, x.Item2));

    await Assert.That(() => PropertyCheck.Run(nameof(Metadata_AnyFlagCombination_KeepsTrackingAndPrecisionConsistentAsync), property, maxTest: 500, seed: 0x5EED_F1A6))
      .ThrowsNothing();
  }

  private static bool _metadataHolds(Guid value, GuidMetadatas flags, GuidMetadatas otherFlags) {
    var tracked = TrackedGuid.FromIntercepted(value, flags);
    var sameValue = TrackedGuid.FromIntercepted(value, otherFlags);
    var withoutCreationSources = TrackedGuid.FromIntercepted(value, flags & ~CREATION_SOURCES);

    return tracked.Metadata == flags
      && (!tracked.SubMillisecondPrecision || tracked.IsTracking)
      && tracked.IsTracking == ((flags & CREATION_SOURCES) != 0)
      && !withoutCreationSources.IsTracking
      && tracked.IsTimeOrdered == flags.HasFlag(GuidMetadatas.Version7)
      && tracked == sameValue
      && tracked.GetHashCode() == sameValue.GetHashCode();
  }

  /// <summary>
  /// For every Guid under every culture: <see cref="TrackedGuidJsonConverter"/> and the generated
  /// <see cref="MessageIdJsonConverter"/> write the plain lowercase "D" form as a JSON string and read it back to
  /// the same value. A read id is external (not tracking), because provenance does not survive serialization.
  /// <see cref="MessageId"/> accepts only version 7 ids, so its half of the check uses the generated Guid with the
  /// version and variant bits set.
  /// </summary>
  [Test]
  public async Task JsonConverters_AnyGuidAnyCulture_RoundTripAsPlainStringAsync() {
    var input = ArbMap.Default.GeneratorFor<Guid>().Zip(Gen.Elements(_cultureNames)).ToArbitrary();
    var property = Prop.ForAll(input, x => _roundTrips(x.Item1, x.Item2));

    await Assert.That(() => PropertyCheck.Run(nameof(JsonConverters_AnyGuidAnyCulture_RoundTripAsPlainStringAsync), property, maxTest: 300, seed: 0x5EED_1503))
      .ThrowsNothing();
  }

  private static bool _roundTrips(Guid value, string cultureName) {
    var original = CultureInfo.CurrentCulture;
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
    try {
      var options = new JsonSerializerOptions { Converters = { new TrackedGuidJsonConverter(), new MessageIdJsonConverter() } };
      var v7 = _asVersion7(value);

      var trackedJson = JsonSerializer.Serialize(TrackedGuid.FromExternal(value), options);
      var messageIdJson = JsonSerializer.Serialize(MessageId.From(v7), options);
      var tracked = JsonSerializer.Deserialize<TrackedGuid>(trackedJson, options);
      var messageId = JsonSerializer.Deserialize<MessageId>(messageIdJson, options);

      return trackedJson == _plainJsonString(value)
        && messageIdJson == _plainJsonString(v7)
        && tracked.Value == value
        && !tracked.IsTracking
        && messageId.Value == v7;
    } finally {
      CultureInfo.CurrentCulture = original;
    }
  }

  private static string _plainJsonString(Guid value) => $"\"{value.ToString("D", CultureInfo.InvariantCulture)}\"";

  private static Guid _asVersion7(Guid value) {
    var bytes = value.ToByteArray(bigEndian: true);
    bytes[6] = (byte)(0x70 | (bytes[6] & 0x0F));
    bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F));
    return new Guid(bytes, bigEndian: true);
  }
}
