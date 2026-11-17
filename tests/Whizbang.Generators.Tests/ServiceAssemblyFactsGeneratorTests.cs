// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Every generated assembly records the facts a host decides its service from (#2243): its name, whether it is a
/// service root, and the generated assemblies it was compiled against.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/ServiceAssemblyFactsGenerator.cs</code-under-test>
public class ServiceAssemblyFactsGeneratorTests {
  private const string FACTS_FILE = "WhizbangServiceAssemblyFacts.g.cs";

  private const string PLAIN_SOURCE = """
    namespace Orders;

    public sealed class Marker;
    """;

  private static GeneratorDriverRunResult _run(CSharpCompilation compilation) =>
    GeneratorTestHelper.RunGenerator<ServiceAssemblyFactsGenerator>(compilation, []);

  private static CSharpCompilation _compilation(string source, string assemblyName, OutputKind kind, params MetadataReference[] extra) {
    var compilation = GeneratorTestHelper.CreateCompilation(source, assemblyName).WithOptions(new CSharpCompilationOptions(kind));
    return compilation.AddReferences(extra);
  }

  [Test]
  public async Task ALibrary_RecordsItsName_NotARoot_AndTheGeneratedAssembliesItReferencesAsync() {
    var result = _run(_compilation(PLAIN_SOURCE, "Orders.Contracts", OutputKind.DynamicallyLinkedLibrary));

    var facts = GeneratorTestHelper.GetRequiredGeneratedSource(result, FACTS_FILE);

    await Assert.That(facts).Contains("[assembly: global::Whizbang.Core.WhizbangServiceAssembly]");
    await Assert.That(facts).Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]");
    await Assert.That(facts).Contains("global::Whizbang.Core.WhizbangServiceAssemblies.Register(\"Orders.Contracts\", false, new string[] { \"Whizbang.Core\" });")
      .Because("Whizbang.Core is itself compiled with the generators, so it carries the marker and is recorded");
  }

  [Test]
  [Arguments(OutputKind.ConsoleApplication)]
  [Arguments(OutputKind.WindowsApplication)]
  public async Task AnExecutable_IsAServiceRootAsync(OutputKind kind) {
    var result = _run(_compilation(PLAIN_SOURCE + "\npublic static class Program { public static void Main() { } }", "Orders.Api", kind));

    await Assert.That(GeneratorTestHelper.GetRequiredGeneratedSource(result, FACTS_FILE))
      .Contains("Register(\"Orders.Api\", true,");
  }

  [Test]
  public async Task AnAssemblyThatDeclaresAWhizbangDbContext_IsAServiceRootAsync() {
    const string source = """
      namespace Whizbang.Data.EFCore.Custom {
        [System.AttributeUsage(System.AttributeTargets.Class)]
        public sealed class WhizbangDbContextAttribute : System.Attribute;
      }

      namespace Orders.Data {
        [Whizbang.Data.EFCore.Custom.WhizbangDbContext]
        public partial class OrdersDbContext;
      }
      """;

    var result = _run(_compilation(source, "Orders.Data", OutputKind.DynamicallyLinkedLibrary));

    await Assert.That(GeneratorTestHelper.GetRequiredGeneratedSource(result, FACTS_FILE))
      .Contains("Register(\"Orders.Data\", true,");
  }

  [Test]
  public async Task OnlyReferencesThatCarryTheMarker_AreRecordedAsync() {
    // A referenced assembly compiled with the generators carries the marker; one compiled without them does not.
    var generated = _emitReference("Shared.Generated", "[assembly: Whizbang.Core.WhizbangServiceAssembly]\nnamespace Shared; public sealed class A;");
    var plain = _emitReference("Shared.Plain", "namespace Plain; public sealed class B;");

    var result = _run(_compilation(PLAIN_SOURCE, "Orders", OutputKind.DynamicallyLinkedLibrary, generated, plain));
    var facts = GeneratorTestHelper.GetRequiredGeneratedSource(result, FACTS_FILE);

    await Assert.That(facts).Contains("new string[] { \"Shared.Generated\", \"Whizbang.Core\" }")
      .Because("the recorded references are the marked ones, sorted");
    await Assert.That(facts).DoesNotContain("Shared.Plain");
  }

  [Test]
  public async Task ACompilationThatDoesNotReferenceWhizbangCore_RecordsNothingAsync() {
    var compilation = CSharpCompilation.Create(
      "Unrelated", [CSharpSyntaxTree.ParseText(PLAIN_SOURCE)],
      [SharedMetadataReferences.FromFile(typeof(object).Assembly.Location)],
      new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    var result = _run(compilation);

    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, FACTS_FILE)).IsNull();
  }

  [Test]
  public async Task ACompilationWithoutAName_RecordsNothingAsync() {
    var result = GeneratorTestHelper.RunGeneratorOnUnnamedAssembly(new ServiceAssemblyFactsGenerator(), PLAIN_SOURCE);

    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, FACTS_FILE)).IsNull();
  }

  [Test]
  public async Task TheEmittedFacts_Compile_AgainstWhizbangCoreAsync() {
    var compilation = _compilation(PLAIN_SOURCE, "Orders", OutputKind.DynamicallyLinkedLibrary);
    var driver = CSharpGeneratorDriver.Create(new ServiceAssemblyFactsGenerator());
    _ = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

    var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();

    await Assert.That(errors).IsEmpty();
  }

  [Test]
  public async Task Literal_EscapesQuotesAndBackslashesAsync() {
    await Assert.That(ServiceAssemblyFactsGenerator.Literal("a\"b\\c")).IsEqualTo("\"a\\\"b\\\\c\"");
  }

  private static PortableExecutableReference _emitReference(string assemblyName, string source) {
    var compilation = GeneratorTestHelper.CreateCompilation(source, assemblyName);
    using var stream = new MemoryStream();
    var emitted = compilation.Emit(stream);
    if (!emitted.Success) {
      throw new InvalidOperationException(string.Join("; ", emitted.Diagnostics));
    }
    return MetadataReference.CreateFromImage(stream.ToArray());
  }
}
