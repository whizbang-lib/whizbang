// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Issue #1874: the generated message JSON context marks a <c>required</c> member (or one carrying
/// <c>[JsonRequired]</c>) as required on a perspective model and the types only a perspective model reaches, so a
/// stored row missing it is refused instead of read as the member's default. Events, commands and every other
/// message are history or wire traffic: they are read tolerantly, so a later code change that adds <c>required</c>
/// never stops a replay. A type a perspective model shares with an event is read tolerantly too, and its required
/// members are declared to the startup check instead, which reports rows missing them. A required member whose type
/// admits null is never marked: the framework omits a null when writing, so refusing its absence would refuse a value
/// the framework wrote.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.cs</code-under-test>
[Category("SourceGenerators")]
[Category("JsonSerialization")]
public class MessageJsonContextRequiredMemberTests {
  private const string INITIALIZER = "MessageJsonContextInitializer.g.cs";

  /// <summary>Whether the statement after the property's <c>CreateProperty</c> call marks it required.</summary>
  private static bool _marksRequired(string code, string propertyName) =>
    Regex.IsMatch(code,
      $@"CreateProperty<[^>]+>\(\s*options,\s*""{Regex.Escape(propertyName)}"",[^;]*;\s*(?:properties\[\d+\]\.ShouldSerialize = [^\r\n]*\s*)?properties\[\d+\]\.IsRequired = true;",
      RegexOptions.None, TimeSpan.FromSeconds(5));

  private static async Task<(string Context, string Initializer)> _generateAsync(string source) {
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    var initializer = GeneratorTestHelper.GetGeneratedSource(result, INITIALIZER);
    await Assert.That(code).IsNotNull();
    await Assert.That(initializer).IsNotNull();
    return (code!, initializer!);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_RequiredMembersOfAPerspectiveModel_AreMarkedRequired_UnlessTheyAdmitNullAsync() {
    var (code, initializer) = await _generateAsync("""
      #nullable enable
      using System;
      using System.Collections.Generic;
      using System.Text.Json.Serialization;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      namespace TestApp;
      public class Point {
        [JsonPropertyName("x")]
        public required double X { get; init; }
        public required string Name { get; init; }
        public required string? Note { get; init; }
        public required int? Rank { get; init; }
        [JsonRequired]
        public int Weight { get; init; }
        [JsonRequired]
        public string? Optional { get; init; }
        public int Plain { get; init; }
      }
      public record Picture {
        public Guid Id { get; init; }
        public required string Title { get; init; }
        public List<Point> Points { get; init; } = [];
      }
      public record PictureDrawn([property: StreamId] Guid Id) : IEvent;
      public class PictureProjection : IPerspectiveFor<Picture, PictureDrawn> {
        public Picture Apply(Picture currentData, PictureDrawn eventData) => currentData;
      }
      """);

    await Assert.That(_marksRequired(code, "Title")).IsTrue()
      .Because("the perspective model's own required member is refused when a row lacks it");
    await Assert.That(_marksRequired(code, "X")).IsTrue()
      .Because("a type only the model reaches is part of the row; a required value type can never be omitted by the writer");
    await Assert.That(_marksRequired(code, "Name")).IsTrue()
      .Because("a required non-nullable reference is always written");
    await Assert.That(_marksRequired(code, "Weight")).IsTrue()
      .Because("[JsonRequired] asks for the same refusal as the keyword");
    await Assert.That(_marksRequired(code, "Note")).IsFalse()
      .Because("a null is omitted when writing, so a required member that may be null may be absent");
    await Assert.That(_marksRequired(code, "Rank")).IsFalse();
    await Assert.That(_marksRequired(code, "Optional")).IsFalse();
    await Assert.That(_marksRequired(code, "Plain")).IsFalse();
    await Assert.That(code).Contains("throw new global::System.InvalidOperationException(\"X is set through its constructor parameter.\")")
      .Because("System.Text.Json refuses a required property without a setter, even one its constructor parameter fills");
    await Assert.That(code).DoesNotContain("\"Plain is set through its constructor parameter.\"");
    await Assert.That(initializer).DoesNotContain("DeclaredRequiredMembers")
      .Because("a type the reader refuses for itself needs no declaration to the startup check");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_RequiredMembersOfAnEvent_AreNotMarkedRequiredAsync() {
    var (code, initializer) = await _generateAsync("""
      #nullable enable
      using System;
      using System.Collections.Generic;
      using System.Text.Json.Serialization;
      using Whizbang.Core;
      namespace TestApp;
      public class Stroke {
        [JsonPropertyName("w")]
        public required double Width { get; init; }
      }
      public class StrokeDrawn : IEvent {
        [StreamId]
        public Guid Id { get; init; }
        public required string Color { get; init; }
        [JsonRequired]
        public int Layer { get; init; }
        public List<Stroke> Strokes { get; init; } = [];
      }
      """);

    await Assert.That(_marksRequired(code, "Color")).IsFalse()
      .Because("an event is history: one stored before the member became required must still replay");
    await Assert.That(_marksRequired(code, "Layer")).IsFalse();
    await Assert.That(_marksRequired(code, "Width")).IsFalse()
      .Because("a type inside an event is history as much as the event is");
    await Assert.That(code).DoesNotContain("is set through its constructor parameter.")
      .Because("the throwing setter exists only for a property marked required");
    await Assert.That(initializer).DoesNotContain("DeclaredRequiredMembers")
      .Because("no perspective row holds these types, so the startup check has nothing to look for");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_RequiredMembersOfACommandOrSerializableType_AreNotMarkedRequiredAsync() {
    var (code, _) = await _generateAsync("""
      #nullable enable
      using Whizbang;
      using Whizbang.Core;
      namespace TestApp;
      public class Rename : ICommand {
        public required string Name { get; init; }
      }
      [WhizbangSerializable]
      public class Snapshot {
        public required int Count { get; init; }
      }
      """);

    await Assert.That(_marksRequired(code, "Name")).IsFalse()
      .Because("a command in flight when the member became required must still be read");
    await Assert.That(_marksRequired(code, "Count")).IsFalse()
      .Because("only a perspective row is refused for a missing required member");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ATypeAModelSharesWithAnEvent_IsReadTolerantly_AndDeclaredToTheStartupCheckAsync() {
    var (code, initializer) = await _generateAsync("""
      #nullable enable
      using System;
      using System.Collections.Generic;
      using System.Text.Json.Serialization;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      namespace TestApp;
      public record SharedPoint {
        [JsonPropertyName("x")]
        public required double X { get; init; }
        public required double Y { get; init; }
        public required string? Label { get; init; }
        public double Z { get; init; }
      }
      public record SharedTag {
        public string Name { get; init; } = "";
      }
      public record Picture {
        public Guid Id { get; init; }
        public required string Title { get; init; }
        public Dictionary<string, SharedPoint> Anchors { get; init; } = [];
        public SharedTag? Tag { get; init; }
      }
      public record PointsDrawn([property: StreamId] Guid Id, List<SharedPoint> Points, SharedTag Tag) : IEvent;
      public class PictureProjection : IPerspectiveFor<Picture, PointsDrawn> {
        public Picture Apply(Picture currentData, PointsDrawn eventData) => currentData;
      }
      """);

    await Assert.That(_marksRequired(code, "Title")).IsTrue()
      .Because("the model itself is not carried by any event");
    await Assert.That(_marksRequired(code, "X")).IsFalse()
      .Because("the type is also read from history, and one JsonTypeInfo serves both readers");
    await Assert.That(_marksRequired(code, "Y")).IsFalse();
    await Assert.That(code).DoesNotContain("\"X is set through its constructor parameter.\"");
    await Assert.That(initializer).Contains("global::Whizbang.Core.Serialization.DeclaredRequiredMembers.Register(typeof(global::TestApp.SharedPoint), \"Y\", \"x\");")
      .Because("the startup check still reports rows missing them, by their declared names, nullable ones excluded");
    await Assert.That(initializer).DoesNotContain("typeof(global::TestApp.SharedTag)")
      .Because("a shared type with no required member has nothing to declare");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ANestedModelAndAPolymorphicMember_AreMarkedThroughTheModelAsync() {
    var (code, _) = await _generateAsync("""
      #nullable enable
      using System;
      using System.Text.Json.Serialization;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      namespace TestApp;
      [JsonPolymorphic]
      [JsonDerivedType(typeof(Circle), "circle")]
      public abstract record Shape;
      public sealed record Circle : Shape {
        public required double Radius { get; init; }
      }
      public class Board : IPerspectiveFor<Board.Model, Board.Cleared> {
        public record Model {
          public Guid Id { get; init; }
          public required int Size { get; init; }
          public Shape? Focus { get; init; }
        }
        public record Cleared([property: StreamId] Guid Id) : IEvent;
        public Model Apply(Model currentData, Cleared eventData) => currentData;
      }
      """);

    await Assert.That(_marksRequired(code, "Size")).IsTrue()
      .Because("a model nested in its perspective is a perspective model");
    await Assert.That(_marksRequired(code, "Radius")).IsTrue()
      .Because("a derived type of a polymorphic member is reached through that member");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_RequiredReferenceInAnObliviousContext_IsLeftOptionalAsync() {
    var (code, _) = await _generateAsync("""
      #nullable disable
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      namespace TestApp;
      public class Labeled {
        public Guid Id { get; init; }
        public required string Label { get; init; }
        public required int Count { get; init; }
      }
      public record Relabeled([property: StreamId] Guid Id) : IEvent;
      public class LabeledProjection : IPerspectiveFor<Labeled, Relabeled> {
        public Labeled Apply(Labeled currentData, Relabeled eventData) => currentData;
      }
      """);

    await Assert.That(_marksRequired(code, "Label")).IsFalse()
      .Because("without nullable annotations the reference may be null, and a null is omitted when writing");
    await Assert.That(_marksRequired(code, "Count")).IsTrue();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ADeclarationOfASharedType_CompilesAsync() {
    // The context's facade references WhizbangIdJsonContext, which WhizbangIdGenerator emits, so both run.
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors([new MessageJsonContextGenerator(), new WhizbangIdGenerator()], """
      #nullable enable
      using System;
      using System.Collections.Generic;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      namespace TestApp;
      public record SharedPoint {
        public required double X { get; init; }
      }
      public record Picture {
        public Guid Id { get; init; }
        public List<SharedPoint> Points { get; init; } = [];
      }
      public record PointsDrawn([property: StreamId] Guid Id, List<SharedPoint> Points) : IEvent;
      public class PictureProjection : IPerspectiveFor<Picture, PointsDrawn> {
        public Picture Apply(Picture currentData, PointsDrawn eventData) => currentData;
      }
      """);

    await Assert.That(errors).IsEmpty();
  }
}
