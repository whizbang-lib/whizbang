// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
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

/// <summary>A command with a required member, as one in flight before the member became required carried it.</summary>
public sealed record DeclaredCaseRenameCommand : ICommand {
  /// <summary>Required.</summary>
  public required string Name { get; init; }

  /// <summary>Required by attribute.</summary>
  [JsonRequired]
  public int Revision { get; init; }
}

/// <summary>A point only the sketch row carries.</summary>
public sealed record DeclaredCaseSketchPoint {
  /// <summary>Declared as <c>x</c>.</summary>
  [JsonPropertyName("x")]
  public required double X { get; init; }

  /// <summary>Required, but its type admits null, which the framework omits when writing.</summary>
  public required string? Label { get; init; }

  /// <summary>Required by attribute rather than by the keyword.</summary>
  [JsonRequired]
  public int Weight { get; init; }
}

/// <summary>A point the sketch row shares with an event.</summary>
public sealed record DeclaredCaseSharedPoint {
  /// <summary>Declared as <c>x</c>.</summary>
  [JsonPropertyName("x")]
  public required double X { get; init; }

  /// <summary>Optional.</summary>
  public double Z { get; init; }
}

/// <summary>A perspective row: derived data the startup pass rewrites.</summary>
public sealed record DeclaredCaseSketch {
  /// <summary>The stream.</summary>
  [StreamId]
  public Guid Id { get; init; }

  /// <summary>Declared as <c>title</c>.</summary>
  [JsonPropertyName("title")]
  public required string Title { get; init; }

  /// <summary>Points only the row carries.</summary>
  public List<DeclaredCaseSketchPoint> Points { get; init; } = [];

  /// <summary>Points an event also carries.</summary>
  public List<DeclaredCaseSharedPoint> Anchors { get; init; } = [];
}

/// <summary>An event carrying the shared points.</summary>
/// <param name="Id">The stream.</param>
/// <param name="Anchors">The anchors.</param>
public sealed record DeclaredCaseAnchorsSetEvent([property: StreamId] Guid Id, List<DeclaredCaseSharedPoint> Anchors) : IEvent;

/// <summary>Folds the sketch.</summary>
public sealed class DeclaredCaseSketchProjection : IPerspectiveFor<DeclaredCaseSketch, DeclaredCaseAnchorsSetEvent> {
  /// <inheritdoc/>
  public DeclaredCaseSketch Apply(DeclaredCaseSketch currentData, DeclaredCaseAnchorsSetEvent eventData) =>
    currentData with { Anchors = eventData.Anchors };
}

/// <summary>
/// Issue #1874: the framework writes a property under its declared <c>[JsonPropertyName]</c>, and an earlier
/// release wrote it under its C# name. A name that differs only in case is read back, through the options every
/// event store, transport and perspective reader is built from, rather than silently left at its default. A
/// <c>required</c> member missing altogether is refused on a perspective row and on the types only a row carries; an
/// event, a command, and a type a row shares with an event read it as its default, because history must stay readable
/// after a later change adds <c>required</c>.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Serialization/JsonContextRegistry.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Serialization/DeclaredRequiredMembers.cs</code-under-test>
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
  public async Task ARequiredMemberMissingFromAnEvent_ReadsAsItsDefaultAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();

    var read = JsonSerializer.Deserialize<DeclaredCasePointsDrawnEvent>(
      """{"DrawingId":"0199a0f0-0000-7000-8000-000000001874","pts":[{"y":2,"Label":"a"}]}""", options)!;

    await Assert.That(read.Points[0].X).IsEqualTo(0d)
      .Because("an event stored before the member became required is history, and history must replay");
    await Assert.That(read.Points[0].Weight).IsEqualTo(0)
      .Because("[JsonRequired] on an event's type is tolerated the same way");
    await Assert.That(read.Points[0].Y).IsEqualTo(2d);
  }

  [Test]
  public async Task ARequiredMemberMissingFromACommand_ReadsAsItsDefaultAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();

    var read = JsonSerializer.Deserialize<DeclaredCaseRenameCommand>("{}", options)!;

    await Assert.That(read.Name).IsNull()
      .Because("a command in flight when the member became required must still be read; the compiler guards new ones");
    await Assert.That(read.Revision).IsEqualTo(0);
  }

  [Test]
  public async Task ARequiredMemberMissingFromAPerspectiveRow_IsRefused_NotDefaultedAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();

    var failure = await Assert.That(() => JsonSerializer.Deserialize<DeclaredCaseSketch>("""{"Points":[]}""", options))
      .Throws<JsonException>();

    await Assert.That(failure!.Message).Contains("title")
      .Because("the refusal names the missing member, rather than the reader handing back an empty title");
  }

  [Test]
  public async Task ARequiredMemberMissingFromATypeOnlyARowCarries_IsRefusedAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();

    await Assert.That(() => JsonSerializer.Deserialize<DeclaredCaseSketch>("""{"title":"t","Points":[{"Weight":1}]}""", options))
      .Throws<JsonException>();
    await Assert.That(() => JsonSerializer.Deserialize<DeclaredCaseSketchPoint>("""{"x":1}""", options))
      .Throws<JsonException>()
      .Because("[JsonRequired] asks for the same refusal as the keyword");
  }

  [Test]
  public async Task ATypeARowSharesWithAnEvent_ReadsTolerantly_AndIsDeclaredToTheStartupCheckAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();

    var read = JsonSerializer.Deserialize<DeclaredCaseSketch>("""{"title":"t","Anchors":[{"Z":4}]}""", options)!;

    await Assert.That(read.Anchors[0].X).IsEqualTo(0d)
      .Because("one JsonTypeInfo serves the row and the event, and the event's history must stay readable");
    await Assert.That(DeclaredRequiredMembers.IsDeclared(typeof(DeclaredCaseSharedPoint), "x")).IsTrue()
      .Because("the startup check still reports rows missing it, at Error");
    await Assert.That(DeclaredRequiredMembers.IsDeclared(typeof(DeclaredCaseSharedPoint), "Z")).IsFalse();
    await Assert.That(DeclaredRequiredMembers.IsDeclared(typeof(DeclaredCasePoint), "x")).IsFalse()
      .Because("a type no row carries has nothing for the startup check to look for");
  }

  [Test]
  public async Task ARequiredMemberWhoseTypeAdmitsNull_RoundTripsAsNullAsync() {
    var options = JsonContextRegistry.CreateCombinedOptions();
    var point = new DeclaredCaseSketchPoint { X = 1, Label = null, Weight = 4 };

    var json = JsonSerializer.Serialize(point, options);
    var read = JsonSerializer.Deserialize<DeclaredCaseSketchPoint>(json, options)!;

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
