// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using static Whizbang.Generators.Tests.PerspectiveContractTestHarness;

namespace Whizbang.Generators.Tests;

/// <summary>
/// What a perspective adds to its assembly's generated source (#2401, #1689, #2469). The runner logic every perspective
/// shares is the generic base in Whizbang.Core, which no assembly generates; each runner emits only what differs between
/// perspectives (its event list, its Apply dispatch, its empty model, its registrations). A runner used to repeat about
/// 190 KB of near-constant code, so compile time, memory and the generator's own time grew by that much per perspective.
/// </summary>
/// <remarks>
/// Sizes stand in for timing here: tests are deterministic, so the generator's time is measured by the build-cost
/// benchmark instead, and these pin the cause of it, the source emitted per perspective.
/// </remarks>
/// <code-under-test>src/Whizbang.Generators/PerspectiveRunnerGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRunnerBase.cs</code-under-test>
[Category("SourceGenerators")]
public class PerspectiveRunnerGeneratedSizeTests {
  // A simple runner is about 3 KB; the bound leaves room for the per-perspective parts to grow.
  private const int MAX_RUNNER_BYTES = 12 * 1024;

  private const string ORDER_PROJECTION = """
      public class OrderProjection : IPerspectiveFor<Order, OrderPlaced, OrderStatusChanged> {
        public Order Apply(Order current, OrderPlaced @event) => current;
        public Order Apply(Order current, OrderStatusChanged @event) => current with { Status = @event.Status };
      }
      """;

  private const string INVOICE_PROJECTION = """
      public class InvoiceProjection : IPerspectiveFor<Invoice, InvoiceIssued> {
        public Invoice Apply(Invoice current, InvoiceIssued @event) => current;
      }
      """;

  [RequiresAssemblyFiles]
  private static long _generatedBytes(string perspectives) {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(PREAMBLE + perspectives);
    return GeneratorTestHelper.GetAllGeneratedSources(result).Sum(static s => (long)s.Source.Length);
  }

  /// <summary>A runner emits only its perspective's own parts, not the runner logic every perspective shares.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ARunner_EmitsOnlyWhatItsPerspectiveVaries_NotTheSharedRunnerLogicAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(PREAMBLE + ORDER_PROJECTION);
    var runner = GeneratorTestHelper.GetRequiredGeneratedSource(result, "OrderProjectionRunner.g.cs");

    await Assert.That(runner.Length).IsLessThan(MAX_RUNNER_BYTES)
      .Because("the shared runner logic lives in the generic base in Whizbang.Core");
    await Assert.That(runner)
      .Contains(": global::Whizbang.Core.Perspectives.PerspectiveRunnerBase<global::TestApp.OrderProjection, global::TestApp.Order>")
      .Because("a runner derives from the base Whizbang.Core ships (#2469), not from one generated into its assembly");
  }

  /// <summary>
  /// A second perspective adds its runner and nothing else, and no assembly is given a copy of the shared base: it is
  /// library code in Whizbang.Core (#2469), one copy for every consumer, under the library's coverage gate.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ASecondPerspective_AddsOnlyItsOwnRunner_NoRunnerBaseIsGeneratedAsync() {
    var one = _generatedBytes(ORDER_PROJECTION);
    var two = _generatedBytes(ORDER_PROJECTION + INVOICE_PROJECTION);

    await Assert.That(two - one).IsLessThan(MAX_RUNNER_BYTES)
      .Because("each further perspective costs only its own runner");

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(PREAMBLE + ORDER_PROJECTION + INVOICE_PROJECTION);
    var bases = GeneratorTestHelper.GetAllGeneratedSources(result)
      .Count(static s => s.FileName.Contains("PerspectiveRunnerBase", StringComparison.Ordinal));
    await Assert.That(bases).IsEqualTo(0)
      .Because("the runner base is Whizbang.Core's, so generating a copy would duplicate it in every consumer");
  }

  /// <summary>An assembly with no perspective gets no runner base: it would be dead code there.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnAssemblyWithNoPerspective_GetsNoRunnerBaseAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(PREAMBLE);

    await Assert.That(GeneratorTestHelper.GetAllGeneratedSources(result)).IsEmpty();
  }

  /// <summary>The runners of two perspectives compile against the shared base in Whizbang.Core.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task TwoRunnersAndTheirSharedBase_CompileAsync() {
    var source = "global using System.Linq;\n" + PREAMBLE + ORDER_PROJECTION + INVOICE_PROJECTION;

    var (_, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(new PerspectiveRunnerGenerator(), source, "TwoRunners");
    var errors = output.GetDiagnostics()
      .Where(static d => d.Severity == DiagnosticSeverity.Error)
      .Select(static d => d.ToString())
      .ToArray();

    await Assert.That(errors).IsEmpty();
  }
}
