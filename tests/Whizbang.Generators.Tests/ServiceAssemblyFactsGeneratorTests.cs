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
  public async Task AReferencedTurnkeyPackage_IsRegistered_FromTheModuleInitializerAsync() {
    // #2502: a package no code names is never loaded, so the consumer's own module initializer calls its registrar.
    var package = _emitReference("Storage.Package", """
      [assembly: Whizbang.Core.WhizbangTurnkeyPackage(typeof(Storage.Turnkey))]
      [assembly: Whizbang.Core.WhizbangTurnkeyPackage(typeof(Storage.Turnkey))]
      [assembly: Whizbang.Core.WhizbangTurnkeyPackage(typeof(Storage.NoRegister))]
      [assembly: Whizbang.Core.WhizbangTurnkeyPackage(typeof(Storage.InstanceRegister))]
      [assembly: Whizbang.Core.WhizbangTurnkeyPackage(typeof(Storage.Hidden))]
      [assembly: Whizbang.Core.WhizbangTurnkeyPackage(typeof(Storage.TakesArguments))]
      [assembly: Whizbang.Core.WhizbangTurnkeyPackage(typeof(Storage.PrivateRegister))]
      [assembly: Whizbang.Core.WhizbangTurnkeyPackage(typeof(Storage.Another))]
      namespace Storage;
      public static class Turnkey { public static void Register() { } }
      public static class Another { public static void Register() { } }
      public static class NoRegister { public static void Other() { } }
      public sealed class InstanceRegister { public void Register() { } }
      internal static class Hidden { public static void Register() { } }
      public static class TakesArguments { public static void Register(int x) { } }
      public static class PrivateRegister { private static void Register() { } }
      """);

    var result = _run(_compilation(PLAIN_SOURCE, "Orders", OutputKind.ConsoleApplication, package));
    var facts = GeneratorTestHelper.GetRequiredGeneratedSource(result, FACTS_FILE);
    var compiles = _compilation(PLAIN_SOURCE, "Orders", OutputKind.DynamicallyLinkedLibrary, package);
    _ = CSharpGeneratorDriver.Create(new ServiceAssemblyFactsGenerator()).RunGeneratorsAndUpdateCompilation(compiles, out var output, out _);

    await Assert.That(facts.Split("global::Storage.Turnkey.Register();").Length).IsEqualTo(2).Because("each registrar is called once");
    await Assert.That(facts.IndexOf("global::Storage.Another.Register();", StringComparison.Ordinal))
      .IsGreaterThan(0).And.IsLessThan(facts.IndexOf("global::Storage.Turnkey.Register();", StringComparison.Ordinal))
      .Because("registrars are called in a fixed order");
    await Assert.That(facts).DoesNotContain("NoRegister").And.DoesNotContain("InstanceRegister").And.DoesNotContain("Hidden")
      .And.DoesNotContain("TakesArguments").And.DoesNotContain("PrivateRegister")
      .Because("only a public type with a public static, parameterless Register() can be called");
    await Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
  }

  [Test]
  public async Task ATurnkeyAttributeThatDoesNotNameOneRegistrar_IsSkipped_AndTheOthersAreStillRegisteredAsync() {
    // A reference still being edited (an IDE's live compilation) can carry an attribute that binds to no constructor:
    // no registrar, or one too many arguments. Neither is a registrar the generator can call, and neither may stop the
    // valid one beside it from being registered.
    var package = GeneratorTestHelper.CreateCompilation("""
      [assembly: Whizbang.Core.WhizbangTurnkeyPackage]
      [assembly: Whizbang.Core.WhizbangTurnkeyPackage(typeof(Storage.Broken), typeof(Storage.Broken))]
      [assembly: Whizbang.Core.WhizbangTurnkeyPackage(typeof(Storage.Turnkey))]
      namespace Storage;
      public static class Turnkey { public static void Register() { } }
      public static class Broken { public static void Register() { } }
      """, "Storage.Editing");
    await Assert.That(package.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error)).IsEqualTo(2)
      .Because("the premise: both attributes fail to bind, so the reference carries them in error");

    var result = _run(_compilation(PLAIN_SOURCE, "Orders", OutputKind.ConsoleApplication, package.ToMetadataReference()));
    var facts = GeneratorTestHelper.GetRequiredGeneratedSource(result, FACTS_FILE);

    await Assert.That(result.Results.Single().Exception).IsNull();
    await Assert.That(facts).Contains("global::Storage.Turnkey.Register();");
    await Assert.That(facts).DoesNotContain("Broken").Because("an attribute that names no single registrar names nothing to call");
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
