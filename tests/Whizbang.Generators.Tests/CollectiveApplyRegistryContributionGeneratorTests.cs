// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The collective handler table each assembly generates is registered as that assembly's contribution to the one
/// <c>ICollectiveApplyRegistry</c> the host serves, through a module initializer named by the assembly (#2238), so a
/// host whose handlers live in several assemblies dispatches to all of them and a consumer passes nothing by hand.
/// </summary>
/// <tests>Whizbang.Generators/CollectiveApplyDiscoveryGenerator.cs</tests>
[Category("Generators")]
[Category("CollectiveEvents")]
public class CollectiveApplyRegistryContributionGeneratorTests {
  private const string FILE = "CollectiveApplyRegistry.g.cs";

  private const string HANDLER_SOURCE = """
    using Whizbang.Core.Messaging;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    public sealed class JobModel { public string Status { get; set; } = ""; }

    public sealed record ArchiveJobsCollectiveEvent(CollectiveScope Scope) : ICollectiveEvent;

    public sealed class JobPerspective {
      [CollectiveApplyFor]
      public ICollectiveSpec<JobModel> Apply(ArchiveJobsCollectiveEvent e) => null!;
    }
    """;

  private const string NO_HANDLER_SOURCE = """
    namespace TestApp;

    public sealed class Nothing { }
    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task WithHandlers_RegistersTheTableAsTheAssemblysContribution_NamedByTheAssemblyAsync() {
    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(HANDLER_SOURCE);
    var code = GeneratorTestHelper.GetRequiredGeneratedSource(result, FILE);

    await Assert.That(code).Contains("namespace TestAssembly.Generated")
      .Because("the contribution type is named by its assembly, so two assemblies' contributions never share a name");
    await Assert.That(code).Contains("public sealed class GeneratedCollectiveApplyRegistry : global::Whizbang.Core.Perspectives.ICollectiveApplyRegistry");
    await Assert.That(code).Contains("AddCollectiveApplyRegistryContribution<GeneratedCollectiveApplyRegistry>(services)");
    await Assert.That(code).Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]");
    await Assert.That(code).Contains("ServiceRegistrationCallbacks.RegisterCollectiveApplyRegistry(\"TestAssembly\",")
      .Because("named by its assembly, so a host anchored to its service registers only its own service's handlers (#2243)");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task WithHandlers_TheLegacyTableIsTheContributionsTableAsync() {
    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(HANDLER_SOURCE);
    var code = GeneratorTestHelper.GetRequiredGeneratedSource(result, FILE);

    await Assert.That(code).Contains("public static class CollectiveApplyRegistry")
      .Because("a consumer that still passes CollectiveApplyRegistry.Entries keeps compiling");
    await Assert.That(code).Contains("Entries = global::TestAssembly.Generated.GeneratedCollectiveApplyRegistry.All;")
      .Because("one table per assembly: the legacy name reads the contribution's, never a second copy");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task WithoutHandlers_RegistersNothing_AndKeepsTheEmptyLegacyTableAsync() {
    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(NO_HANDLER_SOURCE);
    var code = GeneratorTestHelper.GetRequiredGeneratedSource(result, FILE);

    await Assert.That(code).Contains("public static class CollectiveApplyRegistry");
    await Assert.That(code).DoesNotContain("GeneratedCollectiveApplyRegistry")
      .Because("an assembly without handlers has nothing to contribute");
    await Assert.That(code).DoesNotContain("RegisterCollectiveApplyRegistry");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task WithHandlers_TheGeneratedCodeCompilesAsync() {
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<CollectiveApplyDiscoveryGenerator>(HANDLER_SOURCE);

    await Assert.That(errors).IsEmpty()
      .Because(string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task TwoAssembliesWithHandlers_OneCompiledAgainstTheOther_BothCompileAsync() {
    var (_, first) = GeneratorTestHelper.RunGeneratorWithFullReferences(new CollectiveApplyDiscoveryGenerator(), HANDLER_SOURCE, "Orders.Handlers");
    await using var image = new MemoryStream();
    var emitted = first.Emit(image);
    await Assert.That(emitted.Success).IsTrue()
      .Because(string.Join(Environment.NewLine, emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

    const string second = """
      using Whizbang.Core.Messaging;
      using Whizbang.Core.Perspectives;

      namespace Billing;

      public sealed class InvoiceModel { public string Status { get; set; } = ""; }

      public sealed record VoidInvoicesCollectiveEvent(CollectiveScope Scope) : ICollectiveEvent;

      public sealed class InvoicePerspective {
        [CollectiveApplyFor]
        public ICollectiveSpec<InvoiceModel> Apply(VoidInvoicesCollectiveEvent e) => null!;
      }
      """;
    var (result, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(
      new CollectiveApplyDiscoveryGenerator(), second, "Billing.Handlers",
      [MetadataReference.CreateFromImage(image.ToArray())]);
    var code = GeneratorTestHelper.GetRequiredGeneratedSource(result, FILE);

    await Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
    await Assert.That(code).Contains("RegisterCollectiveApplyRegistry(\"Billing.Handlers\",");
    await Assert.That(code).Contains("namespace Billing.Handlers.Generated");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnUnnamedAssembly_IsRegisteredUnderTheFallbackNameAsync() {
    var result = GeneratorTestHelper.RunGeneratorOnUnnamedAssembly(new CollectiveApplyDiscoveryGenerator(), HANDLER_SOURCE);
    var code = GeneratorTestHelper.GetRequiredGeneratedSource(result, FILE);

    await Assert.That(code).Contains("namespace UnknownAssembly.Generated");
    await Assert.That(code).Contains("RegisterCollectiveApplyRegistry(\"UnknownAssembly\",");
  }
}
