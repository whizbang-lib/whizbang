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
/// Issue #2624 (V1 of the vector storage plan): the generated message JSON context attaches the stored-vector
/// converter, which reads a JSON array or packed binary32 and writes the array, to every <c>float[]</c> and
/// <c>IReadOnlyList&lt;float&gt;</c> property not marked <c>[StoredAsJsonArray]</c>, and registers each event type's
/// vector paths. A <c>double[]</c> is never a vector: its values do not fit binary32 exactly. A vector inside a
/// collection element, behind a polymorphic member or in a type that refers back to itself gets no path, and WHIZ064
/// says so.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.StoredVectors.cs</code-under-test>
[Category("SourceGenerators")]
[Category("JsonSerialization")]
public class MessageJsonContextStoredVectorTests {
  private const string HEADER = """
    #nullable enable
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    using Whizbang.Core;
    using Whizbang.Core.Attributes;
    namespace TestApp;
    """;

  private static async Task<(string Context, string Initializer, ImmutableDiagnostics Diagnostics)> _generateAsync(string source) {
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(HEADER + source);
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var context = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    var initializer = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContextInitializer.g.cs");
    await Assert.That(context).IsNotNull();
    await Assert.That(initializer).IsNotNull();
    return (context!, initializer!, new ImmutableDiagnostics([.. result.Diagnostics]));
  }

  /// <summary>The diagnostics a run reported.</summary>
  internal sealed record ImmutableDiagnostics(Diagnostic[] All) {
    public Diagnostic[] WithId(string id) => [.. All.Where(d => d.Id == id)];
  }

  /// <summary>The converter factory attached to the property with JSON name <paramref name="jsonName"/>, or null.</summary>
  private static string? _converterFor(string code, string jsonName) {
    var match = Regex.Match(code,
      $@"CustomConverter = global::Whizbang\.Core\.Serialization\.StoredVectorConverters\.(\w+)\(""{Regex.Escape(jsonName)}""",
      RegexOptions.None, TimeSpan.FromSeconds(5));
    return match.Success ? match.Groups[1].Value : null;
  }

  /// <summary>The registration line for <paramref name="eventType"/>, or null when it has none.</summary>
  private static string? _registrationFor(string initializer, string eventType) {
    var match = Regex.Match(initializer,
      $@"StoredVectorPaths\.Register\(typeof\(global::TestApp\.{Regex.Escape(eventType)}\),([^;]*)\);",
      RegexOptions.None, TimeSpan.FromSeconds(5));
    return match.Success ? match.Groups[1].Value.Trim() : null;
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_AFloatArrayOnAnEvent_GetsTheConverterAndAPathAsync() {
    var (context, initializer, _) = await _generateAsync("""
      public record PassageEmbedded(Guid Id, float[] Embedding) : IEvent;
      """);

    await Assert.That(_converterFor(context, "Embedding")).IsEqualTo("ForArray");
    await Assert.That(_registrationFor(initializer, "PassageEmbedded")).IsEqualTo("""["Embedding"]""");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_AReadOnlyListOfFloat_GetsTheListConverter_UnderItsJsonNameAsync() {
    var (context, initializer, _) = await _generateAsync("""
      public record PassageEmbedded(Guid Id, [property: JsonPropertyName("e\"mb")] IReadOnlyList<float>? Embedding) : IEvent;
      """);

    await Assert.That(_converterFor(context, "e\\\"mb")).IsEqualTo("ForReadOnlyList")
      .Because("the path and the converter name the property as it is stored, its [JsonPropertyName]");
    await Assert.That(_registrationFor(initializer, "PassageEmbedded")).IsEqualTo("""["e\"mb"]""");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_DoublesAndOtherFloatCollections_AreNeverVectorsAsync() {
    var (context, initializer, _) = await _generateAsync("""
      public record Measured(Guid Id, double[] Precise, float?[] Sparse, List<float> Listed, IReadOnlyList<double> Doubles, float Single) : IEvent;
      """);

    await Assert.That(context).DoesNotContain("StoredVectorConverters")
      .Because("a double does not fit binary32 exactly, and only float[] and IReadOnlyList<float> are vector types");
    await Assert.That(_registrationFor(initializer, "Measured")).IsNull();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_AnOptedOutVector_GetsNoConverterAndNoPathAsync() {
    var (context, initializer, _) = await _generateAsync("""
      public record PassageEmbedded(Guid Id, float[] Embedding, [property: StoredAsJsonArray] float[] Centroid) : IEvent;
      """);

    await Assert.That(_converterFor(context, "Centroid")).IsNull();
    await Assert.That(_registrationFor(initializer, "PassageEmbedded")).IsEqualTo("""["Embedding"]""");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ANestedObjectsVector_GetsTheSegmentedPathAsync() {
    var (context, initializer, _) = await _generateAsync("""
      public record Chunk(string Text, float[] Embedding);
      public record Section(Chunk Lead, [property: JsonPropertyName("tail")] Chunk? Tail);
      public record PassageEmbedded(Guid Id, Section Body, float[] Title) : IEvent;
      """);

    await Assert.That(_converterFor(context, "Embedding")).IsEqualTo("ForArray")
      .Because("the nested type's own metadata carries the converter, which every type reaching it shares");
    await Assert.That(_registrationFor(initializer, "PassageEmbedded"))
      .IsEqualTo("""["Body", "Lead", "Embedding"], ["Body", "tail", "Embedding"], ["Title"]""");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_AVectorInsideACollectionElement_GetsNoPath_AndIsReportedAsync() {
    var (_, initializer, diagnostics) = await _generateAsync("""
      public record Chunk(string Text, float[] Embedding);
      public record PassageEmbedded(Guid Id, List<Chunk> Chunks, Dictionary<string, Chunk> Named) : IEvent;
      """);

    await Assert.That(_registrationFor(initializer, "PassageEmbedded")).IsNull()
      .Because("arrays of objects that contain vectors are not packed in 1.0");
    var reported = diagnostics.WithId("WHIZ064");
    await Assert.That(reported.Length).IsEqualTo(2);
    await Assert.That(reported.All(d => d.Severity == DiagnosticSeverity.Info)).IsTrue();
    await Assert.That(reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("Chunks").And.Contains("PassageEmbedded").And.Contains("collection");
    await Assert.That(reported[1].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("Named");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ACollectionOfObjectsWithoutVectors_IsNotReportedAsync() {
    var (_, _, diagnostics) = await _generateAsync("""
      public record Tag(string Name, double[] Weights);
      public record Tagged(Guid Id, List<Tag> Tags, List<string> Labels) : IEvent;
      """);

    await Assert.That(diagnostics.WithId("WHIZ064")).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ACycleOrPolymorphicMemberWithoutVectors_IsNotReportedAsync() {
    var (_, _, diagnostics) = await _generateAsync("""
      public record Folder(string Name, Folder? Parent);
      [JsonPolymorphic]
      [JsonDerivedType(typeof(Note), "note")]
      public abstract record Item;
      public sealed record Note(string Text) : Item;
      public record Filed(Guid Id, Folder Folder, Item Item) : IEvent;
      """);

    await Assert.That(diagnostics.WithId("WHIZ064")).IsEmpty()
      .Because("only a member that actually reaches a vector stays an unpacked vector");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ATypeThatRefersBackToItself_StopsAtTheCycle_AndIsReportedAsync() {
    var (_, initializer, diagnostics) = await _generateAsync("""
      public record Node(float[] Embedding, Node? Parent);
      public record NodeAdded(Guid Id, Node Root) : IEvent;
      """);

    await Assert.That(_registrationFor(initializer, "NodeAdded")).IsEqualTo("""["Root", "Embedding"]""")
      .Because("the walk stops where a type repeats on its own path, so the generator terminates");
    var reported = diagnostics.WithId("WHIZ064");
    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("Root.Parent").And.Contains("refers back");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_AVectorBehindAPolymorphicMember_GetsNoPath_AndIsReportedAsync() {
    var (_, initializer, diagnostics) = await _generateAsync("""
      [JsonPolymorphic]
      [JsonDerivedType(typeof(TextPart), "text")]
      public abstract record Part;
      public sealed record TextPart(string Text, float[] Embedding) : Part;
      public record Composed(Guid Id, Part Body) : IEvent;
      """);

    await Assert.That(_registrationFor(initializer, "Composed")).IsNull();
    var reported = diagnostics.WithId("WHIZ064");
    await Assert.That(reported.Length).IsEqualTo(1);
    await Assert.That(reported[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("Body").And.Contains("polymorphic");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ANestedOrCollectedTypesVector_GetsTheConverterAsync() {
    var (context, _, _) = await _generateAsync("""
      public record Chunk(string Text, float[] ChunkEmbedding);
      public record Section(Chunk Lead);
      public record PassageEmbedded(Guid Id, Section Body, List<Chunk> Chunks) : IEvent;
      """);

    await Assert.That(_converterFor(context, "ChunkEmbedding")).IsEqualTo("ForArray")
      .Because("the nested type's own metadata carries the converter, which every type reaching it shares");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ACommandsVector_GetsTheConverterButNoPathAsync() {
    var (context, initializer, _) = await _generateAsync("""
      public record EmbedPassage(Guid Id, float[] Query) : ICommand;
      """);

    await Assert.That(_converterFor(context, "Query")).IsEqualTo("ForArray")
      .Because("one reader reads either form wherever a type appears; only event bodies are stored, so only events get paths");
    await Assert.That(initializer).DoesNotContain("StoredVectorPaths");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_TheStoredVectorCode_CompilesAsync() {
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors([new MessageJsonContextGenerator(), new WhizbangIdGenerator()], HEADER + """

      public record Chunk(string Text, float[] Embedding, IReadOnlyList<float>? Alternate);
      public record PassageEmbedded(Guid Id, Chunk Lead, float[]? Title, [property: StoredAsJsonArray] float[] Raw) : IEvent;
      public class Passage {
        public float[] Embedding { get; set; } = [];
        public IReadOnlyList<float> Alternate { get; init; } = [];
      }
      public record PassageStored(Guid Id, Passage Passage) : IEvent;
      """);

    await Assert.That(errors).IsEmpty();
  }
}
