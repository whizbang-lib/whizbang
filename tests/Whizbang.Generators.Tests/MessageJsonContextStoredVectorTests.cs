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
/// <c>IReadOnlyList&lt;float&gt;</c> property not marked <c>[StoredAsJsonArray]</c>, wherever the type appears. A
/// <c>double[]</c> is never a vector: its values do not fit binary32 exactly.
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

  private static async Task<string> _generateAsync(string source) {
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(HEADER + source);
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var context = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(context).IsNotNull();
    return context!;
  }

  /// <summary>The converter factory attached to the property with JSON name <paramref name="jsonName"/>, or null.</summary>
  private static string? _converterFor(string code, string jsonName) {
    var match = Regex.Match(code,
      $@"CustomConverter = global::Whizbang\.Core\.Serialization\.StoredVectorConverters\.(\w+)\(""{Regex.Escape(jsonName)}""",
      RegexOptions.None, TimeSpan.FromSeconds(5));
    return match.Success ? match.Groups[1].Value : null;
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_AFloatArrayOnAnEvent_GetsTheConverterAsync() {
    var context = await _generateAsync("""
      public record PassageEmbedded(Guid Id, float[] Embedding) : IEvent;
      """);

    await Assert.That(_converterFor(context, "Embedding")).IsEqualTo("ForArray");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_AReadOnlyListOfFloat_GetsTheListConverter_UnderItsJsonNameAsync() {
    var context = await _generateAsync("""
      public record PassageEmbedded(Guid Id, [property: JsonPropertyName("e\"mb")] IReadOnlyList<float>? Embedding) : IEvent;
      """);

    await Assert.That(_converterFor(context, "e\\\"mb")).IsEqualTo("ForReadOnlyList")
      .Because("the converter names the property as it is stored, its [JsonPropertyName], when a read fails");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_DoublesAndOtherFloatCollections_AreNeverVectorsAsync() {
    var context = await _generateAsync("""
      public record Measured(Guid Id, double[] Precise, float?[] Sparse, List<float> Listed, IReadOnlyList<double> Doubles, float Single) : IEvent;
      """);

    await Assert.That(context).DoesNotContain("StoredVectorConverters")
      .Because("a double does not fit binary32 exactly, and only float[] and IReadOnlyList<float> are vector types");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_AnOptedOutVector_GetsNoConverterAsync() {
    var context = await _generateAsync("""
      public record PassageEmbedded(Guid Id, float[] Embedding, [property: StoredAsJsonArray] float[] Centroid) : IEvent;
      """);

    await Assert.That(_converterFor(context, "Embedding")).IsEqualTo("ForArray");
    await Assert.That(_converterFor(context, "Centroid")).IsNull();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ANestedOrCollectedTypesVector_GetsTheConverterAsync() {
    var context = await _generateAsync("""
      public record Chunk(string Text, float[] ChunkEmbedding);
      public record Section(Chunk Lead);
      public record PassageEmbedded(Guid Id, Section Body, List<Chunk> Chunks) : IEvent;
      """);

    await Assert.That(_converterFor(context, "ChunkEmbedding")).IsEqualTo("ForArray")
      .Because("the nested type's own metadata carries the converter, which every type reaching it shares");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ACommandsVector_GetsTheConverterAsync() {
    var context = await _generateAsync("""
      public record EmbedPassage(Guid Id, float[] Query) : ICommand;
      """);

    await Assert.That(_converterFor(context, "Query")).IsEqualTo("ForArray")
      .Because("one reader reads either form wherever a type appears, and writing is unchanged");
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
