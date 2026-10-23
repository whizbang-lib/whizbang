// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Whizbang.Generators.Tests.BuildCost;

/// <summary>
/// The generators package's targets read the message registry and the pinned-type ledger out of the consumer's
/// BUILT assembly (EmitCompilerGeneratedFiles is off by default), with a byte scan for the JSON a generator embeds
/// as a constant. The contract runs both ways, and both are pinned here with the scanner compiled from the shipped
/// targets file itself: each generator's JSON is found by the opening the targets search for, and comes back
/// byte-identical from an assembly the compiler emitted; and the scan is exact on hand-made constants, including
/// braces and escaped quotes inside string values, and refuses what it cannot be sure of.
/// </summary>
/// <docs>operations/performance/build-cost</docs>
[Category("Generators")]
[Category("BuildCost")]
public class BuildTargetsEmbeddedJsonTests {
  private static readonly Lazy<XDocument> _targets = new(() => XDocument.Load(Path.Combine(
      _findRepoRoot(), "src", "Whizbang.Generators", "build", "SoftwareExtravaganza.Whizbang.Generators.targets")));

  private static string _registryOpening => _targets.Value.Descendants("WhizbangRegistryOpening").Single().Value;
  private static string _ledgerOpening => _targets.Value.Descendants("WhizbangLedgerOpening").Single().Value;

  [Test]
  [RequiresAssemblyFiles]
  public async Task MessageRegistry_EmbeddedJson_IsFoundByTheTargetsAndComesBackByteIdenticalAsync() {
    var (assembly, constant) = _buildWithGenerator(new MessageRegistryGenerator(), "Whizbang.Generated.MessageRegistry");

    var scan = Scanner.Scan(assembly, _registryOpening);

    await Assert.That(scan.Problem).IsNull();
    await Assert.That(scan.Json).IsEqualTo(constant);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task PinnedTypeLedger_EmbeddedJson_IsFoundByTheTargetsAndComesBackByteIdenticalAsync() {
    var (assembly, constant) = _buildWithGenerator(new PinnedTypeLedgerGenerator(), "Whizbang.Generated.PinnedTypeLedgerSource");

    var scan = Scanner.Scan(assembly, _ledgerOpening);

    await Assert.That(scan.Problem).IsNull();
    await Assert.That(scan.Json).IsEqualTo(constant);
  }

  [Test]
  public async Task Scan_BracesAndEscapedQuotesInsideStrings_DoNotEndTheJsonEarlyAsync() {
    const string json = "{\n  \"messages\": [\n    { \"type\": \"A{B}\", \"note\": \"say \\\"}]\\\" \\\\\" }\n  ],\n  \"tail\": \"[{\"\n}";
    var assembly = _emit($$"""internal static class Holder { internal const string Json = {{SymbolDisplay.FormatLiteral(json, true)}}; }""");

    var scan = Scanner.Scan(assembly, _registryOpening);

    await Assert.That(scan.Problem).IsNull();
    await Assert.That(scan.Json).IsEqualTo(json);
  }

  [Test]
  public async Task Scan_TheSameJsonTwice_IsOneMatchAsync() {
    const string json = "{\"messages\": []}";
    var assembly = _emit($$"""
      internal static class Holder {
        internal const string Json = {{SymbolDisplay.FormatLiteral(json, true)}};
        internal static string Read() => Json;
      }
      """);

    var scan = Scanner.Scan(assembly, _registryOpening);

    await Assert.That(scan.Problem).IsNull();
    await Assert.That(scan.Json).IsEqualTo(json).Because("a read of the constant copies it into the user-string heap, and the copy is the same JSON");
  }

  [Test]
  public async Task Scan_TwoDifferentMatches_IsAProblemNotAGuessAsync() {
    var assembly = _emit("""
      internal static class Holder {
        internal const string Json = "{\"messages\": [1]}";
        internal const string Other = "{\"messages\": [2]}";
      }
      """);

    var scan = Scanner.Scan(assembly, _registryOpening);

    await Assert.That(scan.Json).IsNull();
    await Assert.That(scan.Problem).Contains("2 times");
  }

  [Test]
  public async Task Scan_NoMatch_IsNeitherJsonNorAProblemAsync() {
    var assembly = _emit("""internal static class Holder { internal const string Json = "{\"other\": []}"; }""");

    var scan = Scanner.Scan(assembly, _registryOpening);

    await Assert.That(scan.Json).IsNull();
    await Assert.That(scan.Problem).IsNull();
  }

  [Test]
  [Arguments("{\"messages\": [ {\"a\": 1} ")]
  [Arguments("{\"messages\": [ } ]")]
  [Arguments("{\"messages\": [ \u0001 ]}")]
  public async Task Scan_JsonThatDoesNotCloseCleanly_IsAProblemAsync(string broken) {
    var assembly = _emit($$"""internal static class Holder { internal const string Json = {{SymbolDisplay.FormatLiteral(broken, true)}}; }""");

    var scan = Scanner.Scan(assembly, _registryOpening);

    await Assert.That(scan.Json).IsNull();
    await Assert.That(scan.Problem).Contains("matching close brace");
  }

  [Test]
  [Arguments(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 }, true)]
  [Arguments(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 4 }, false)]
  [Arguments(new byte[] { 1, 2 }, new byte[] { 1, 2, 3 }, false)]
  public async Task SameBytes_ComparesLengthAndEveryByteAsync(byte[] left, byte[] right, bool expected) {
    await Assert.That(Scanner.SameBytes(left, right)).IsEqualTo(expected);
  }

  // ---- the generators' side

  [RequiresAssemblyFiles]
  private static (byte[] Assembly, string Constant) _buildWithGenerator(IIncrementalGenerator generator, string holder) {
    const string source = """
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace TestApp;

      [PinnedId("11111111-1111-1111-1111-111111111111")]
      public record OrderPlacedEvent : IEvent { public string Note { get; init; } = "a {brace} and a \"quote\""; }
      """;
    var compilation = CSharpCompilation.Create("Consumer", [CSharpSyntaxTree.ParseText(source)], BuildCostReferences.All(),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    _ = CSharpGeneratorDriver.Create(generator).RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
    var field = (IFieldSymbol)output.GetTypeByMetadataName(holder)!.GetMembers("Json").Single();
    return (_emitCompilation(output), (string)field.ConstantValue!);
  }

  // ---- the scanner, compiled from the shipped targets file

  private static readonly Lazy<Type> _scannerType = new(_compileScanner);

  private static class Scanner {
    public static (string? Json, string? Problem) Scan(byte[] assembly, string opening) {
      var result = _scannerType.Value.GetMethod("Scan")!.Invoke(null, [assembly, opening])!;
      var type = result.GetType();
      return ((string?)type.GetField("Json")!.GetValue(result), (string?)type.GetField("Problem")!.GetValue(result));
    }

    public static bool SameBytes(byte[] left, byte[] right) =>
        (bool)_scannerType.Value.GetMethod("SameBytes")!.Invoke(null, [left, right])!;
  }

  private static Type _compileScanner() {
    var code = _targets.Value.Descendants("UsingTask")
        .Single(t => (string?)t.Attribute("TaskName") == "WhizbangExtractEmbeddedJson")
        .Descendants("Code").Single().Value;
    var scanner = CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot().Members
        .OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "WhizbangEmbeddedJsonScanner");
    var unit = SyntaxFactory.ParseCompilationUnit(
        "using System; using System.Collections.Generic; using System.Text; using System.Text.RegularExpressions;\n" +
        scanner.ToFullString());
    var bytes = _emitCompilation(CSharpCompilation.Create("Scanner", [unit.SyntaxTree], _platform(),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
    var context = new AssemblyLoadContext("whizbang-scanner", isCollectible: true);
    return context.LoadFromStream(new MemoryStream(bytes)).GetType("WhizbangEmbeddedJsonScanner")!;
  }

  private static byte[] _emit(string source) =>
      _emitCompilation(CSharpCompilation.Create("Holder", [CSharpSyntaxTree.ParseText(source)], _platform(),
          new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));

  private static byte[] _emitCompilation(Compilation compilation) {
    using var stream = new MemoryStream();
    var result = compilation.Emit(stream);
    if (!result.Success) {
      throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }
    return stream.ToArray();
  }

  private static ImmutableArray<MetadataReference> _platform() => [
    .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)),
  ];

  private static string _findRepoRoot() {
    var dir = AppContext.BaseDirectory;
    while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "Whizbang.Generators"))) {
      dir = Path.GetDirectoryName(dir);
    }
    return dir ?? throw new InvalidOperationException("Could not locate the repository root from AppContext.BaseDirectory");
  }
}
