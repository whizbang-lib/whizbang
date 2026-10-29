// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Serialization;

/// <summary>A point whose coordinates are declared under lower-case names.</summary>
public sealed record DeclaredCasePoint {
  /// <summary>Declared as <c>x</c>; a release that ignored the attribute wrote <c>X</c>.</summary>
  [JsonPropertyName("x")]
  public required double X { get; init; }

  /// <summary>Declared as <c>y</c>.</summary>
  [JsonPropertyName("y")]
  public required double Y { get; init; }

  /// <summary>Required, but its type admits null, which the framework omits when writing.</summary>
  public required string? Label { get; init; }

  /// <summary>Required by attribute rather than by the keyword.</summary>
  [JsonRequired]
  public int Weight { get; init; }

  /// <summary>Optional: absent reads as the default.</summary>
  public int Order { get; init; }
}

/// <summary>An event carrying points, as an earlier release stored it with C# names.</summary>
/// <param name="DrawingId">The stream.</param>
/// <param name="Points">The points.</param>
public sealed record DeclaredCasePointsDrawnEvent(
    [property: StreamId] Guid DrawingId,
    [property: JsonPropertyName("pts")] List<DeclaredCasePoint> Points) : IEvent;

/// <summary>
/// Issue #1874: the framework writes a property under its declared <c>[JsonPropertyName]</c>, and an earlier
/// release wrote it under its C# name. A name that differs only in case is read back, through the options every
/// event store, transport and perspective reader is built from, rather than silently left at its default; and a
/// <c>required</c> member that is missing altogether is refused rather than defaulted.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Serialization/JsonContextRegistry.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.cs</code-under-test>
[Category("Serialization")]
public class DeclaredNameReadTests {
  private const string OLD_SPELLING =
    """{"DrawingId":"0199a0f0-0000-7000-8000-000000001874","pts":[{"X":327,"Y":362,"Label":null,"Weight":1},{"X":330.7060738825059,"Y":362,"Label":"b","Weight":2}]}""";

  [Test]
  [Arguments(SerializationProfile.Default)]
  [Arguments(SerializationProfile.Persistence)]
  public async Task AnEventStoredWithCSharpNames_ReadsItsValues_UnderEitherProfileAsync(SerializationProfile profile) {
    var options = JsonContextRegistry.CreateCombinedOptions(profile);

    var read = JsonSerializer.Deserialize<DeclaredCasePointsDrawnEvent>(OLD_SPELLING, options)!;

    await Assert.That(read.Points.Count).IsEqualTo(2);
    await Assert.That(read.Points[0].X).IsEqualTo(327d)
      .Because("\"X\" differs from the declared \"x\" only in case, so it is the same member, not a missing one");
    await Assert.That(read.Points[0].Y).IsEqualTo(362d);
    await Assert.That(read.Points[1].X).IsEqualTo(330.7060738825059d);
    await Assert.That(read.Points[1].Label).IsEqualTo("b");
  }

  [Test]
  public async Task TheDeclaredSpelling_IsStillWhatIsWrittenAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var drawn = new DeclaredCasePointsDrawnEvent(Guid.CreateVersion7(), [new DeclaredCasePoint { X = 1, Y = 2, Label = "a", Weight = 3 }]);

    var json = JsonSerializer.Serialize(drawn, options);

    await Assert.That(json).Contains("\"x\":1").And.Contains("\"y\":2").And.Contains("\"pts\":")
      .Because("reading tolerates the old spelling; writing keeps the declared one");
  }

  [Test]
  public async Task ARequiredMemberMissing_IsRefused_NotDefaultedAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();

    var failure = await Assert.That(() => JsonSerializer.Deserialize<DeclaredCasePoint>("""{"y":2,"Label":"a","Weight":1}""", options))
      .Throws<JsonException>();

    await Assert.That(failure!.Message).Contains("x")
      .Because("the refusal names the missing member, rather than the reader handing back 0");
  }

  [Test]
  public async Task AMemberRequiredByAttribute_IsRefusedWhenMissingAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();

    await Assert.That(() => JsonSerializer.Deserialize<DeclaredCasePoint>("""{"x":1,"y":2,"Label":"a"}""", options))
      .Throws<JsonException>();
  }

  [Test]
  public async Task ARequiredMemberWhoseTypeAdmitsNull_RoundTripsAsNullAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var point = new DeclaredCasePoint { X = 1, Y = 2, Label = null, Weight = 4 };

    var json = JsonSerializer.Serialize(point, options);
    var read = JsonSerializer.Deserialize<DeclaredCasePoint>(json, options)!;

    await Assert.That(json).DoesNotContain("Label")
      .Because("the framework omits a null when writing");
    await Assert.That(read.Label).IsNull()
      .Because("so a required member that may be null cannot be refused for being absent, or a value the framework wrote would not read back");
    await Assert.That(read.Weight).IsEqualTo(4);
  }

  [Test]
  public async Task AnOptionalMemberMissing_StillReadsAsItsDefaultAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();

    var read = JsonSerializer.Deserialize<DeclaredCasePoint>("""{"x":1,"y":2,"Weight":1}""", options)!;

    await Assert.That(read.Order).IsEqualTo(0);
  }
}
