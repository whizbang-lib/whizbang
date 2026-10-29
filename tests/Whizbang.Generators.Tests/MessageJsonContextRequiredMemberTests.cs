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
/// <c>[JsonRequired]</c>) as required, as System.Text.Json's own generator does, so a document missing it is
/// refused instead of read as the member's default. A required member whose type admits null is left optional:
/// the framework omits a null when writing, so refusing its absence would refuse a value the framework wrote.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/MessageJsonContextGenerator.cs</code-under-test>
[Category("SourceGenerators")]
[Category("JsonSerialization")]
public class MessageJsonContextRequiredMemberTests {

  /// <summary>Whether the statement after the property's <c>CreateProperty</c> call marks it required.</summary>
  private static bool _marksRequired(string code, string propertyName) =>
    Regex.IsMatch(code,
      $@"CreateProperty<[^>]+>\(\s*options,\s*""{Regex.Escape(propertyName)}"",[^;]*;\s*(?:properties\[\d+\]\.ShouldSerialize = [^\r\n]*\s*)?properties\[\d+\]\.IsRequired = true;",
      RegexOptions.None, TimeSpan.FromSeconds(5));

  private static async Task<string> _generateAsync(string source) {
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs");
    await Assert.That(code).IsNotNull();
    return code!;
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_RequiredMembers_AreMarkedRequired_UnlessTheyAdmitNullAsync() {
    var code = await _generateAsync("""
      #nullable enable
      using System.Text.Json.Serialization;
      using Whizbang.Core;
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
      public class PointsDrawn : IEvent {
        public Point[] Points { get; set; } = [];
      }
      """);

    await Assert.That(_marksRequired(code, "X")).IsTrue()
      .Because("a required value type can never be omitted by the writer, so its absence is a document that lost it");
    await Assert.That(_marksRequired(code, "Name")).IsTrue()
      .Because("a required non-nullable reference is always written");
    await Assert.That(_marksRequired(code, "Weight")).IsTrue()
      .Because("[JsonRequired] asks for the same refusal as the keyword");
    await Assert.That(_marksRequired(code, "Note")).IsFalse()
      .Because("a null is omitted when writing, so a required member that may be null may be absent");
    await Assert.That(_marksRequired(code, "Rank")).IsFalse();
    await Assert.That(_marksRequired(code, "Optional")).IsFalse();
    await Assert.That(_marksRequired(code, "Plain")).IsFalse();
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_RequiredReferenceInAnObliviousContext_IsLeftOptionalAsync() {
    var code = await _generateAsync("""
      #nullable disable
      using Whizbang.Core;
      namespace TestApp;
      public class Labeled : ICommand {
        public required string Label { get; init; }
        public required int Count { get; init; }
      }
      """);

    await Assert.That(_marksRequired(code, "Label")).IsFalse()
      .Because("without nullable annotations the reference may be null, and a null is omitted when writing");
    await Assert.That(_marksRequired(code, "Count")).IsTrue();
  }
}
