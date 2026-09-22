using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// That the harness can never report a clean compile for a generator that did not run.
/// </summary>
/// <remarks>
/// <para>
/// <c>GetGeneratedCompilationErrors</c> runs a generator and returns the resulting compilation's
/// errors. A generator that throws produces no source, and the driver keeps the exception in its own
/// run result rather than in the compilation, so the compilation that comes back is the original one
/// and its error list is empty. An empty error list is exactly what a caller reads as success.
/// </para>
/// <para>
/// That is not a hypothetical. It is the shape of a develop failure in which one half of a
/// comparison reported zero errors while the other reported three: whatever stopped the generator
/// running, the harness reported the result as a perfectly clean compile, so the test failed on the
/// comparison instead of on the thing that actually went wrong. A harness whose silence means
/// either "nothing was wrong" or "nothing ran" cannot be used to conclude anything, and a flake
/// built on it is undiagnosable by construction.
/// </para>
/// </remarks>
[Category("SourceGenerators")]
public class GeneratorTestHelperFailureSurfacingTests {
  private const string SOURCE = """
      namespace TestApp;
      public record Thing;
      """;

  /// <summary>
  /// A generator that cannot generate: it throws from the source-output callback, which is where a
  /// real generator's template reading, symbol walking and string building happen.
  /// </summary>
  private sealed class ThrowingGenerator : IIncrementalGenerator {
    internal const string FAILURE = "this generator cannot generate";

    public void Initialize(IncrementalGeneratorInitializationContext context) =>
      context.RegisterSourceOutput(
        context.CompilationProvider,
        static (_, _) => throw new InvalidOperationException(FAILURE));
  }

  [Test]
  public async Task GetGeneratedCompilationErrors_WhenTheGeneratorThrows_DoesNotReportACleanCompileAsync() {
    // The harness must not answer "no errors" for a run that produced no source. It has to say the
    // generator failed, and say what it threw, because every caller of this helper reads an empty
    // error list as "the generated code is valid".
    await Assert.That(
        () => GeneratorTestHelper.GetGeneratedCompilationErrors<ThrowingGenerator>(SOURCE))
      .Throws<InvalidOperationException>()
      .Because("a generator that threw generated nothing, so the compilation the helper inspects is the "
        + "one it started with and its error list is empty; returning that empty list tells the caller the "
        + "generated code compiled, which is the opposite of what happened. The failure has to reach the "
        + "caller, or a generator that stops running reads as a generator that is working perfectly");
  }

  [Test]
  public async Task GetGeneratedCompilationErrors_WhenTheGeneratorThrows_SaysWhatItThrewAsync() {
    // The message is the whole value of surfacing it: a bare "the generator failed" leaves the next
    // person exactly where this defect left us.
    var thrown = await Assert.That(
        () => GeneratorTestHelper.GetGeneratedCompilationErrors<ThrowingGenerator>(SOURCE))
      .Throws<InvalidOperationException>();

    await Assert.That(thrown!.ToString()).Contains(ThrowingGenerator.FAILURE)
      .Because("the generator's own exception is the only record of why it produced nothing, and the driver "
        + "keeps it out of the compilation, so a harness that does not carry it forward discards the "
        + "diagnosis");
    await Assert.That(thrown!.ToString()).Contains(nameof(ThrowingGenerator))
      .Because("a run with several generators has to name which one failed");
  }

  [Test]
  public async Task GetGeneratedCompilationErrors_WhenTheGeneratorSucceeds_StillReportsTheCompilationErrorsAsync() {
    // The guard must not swallow the helper's actual job. A generator that runs and emits code
    // referencing something absent still has to report that as a compilation error rather than as a
    // generator failure.
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<EmitsBrokenCodeGenerator>(SOURCE);

    await Assert.That(errors.Select(d => d.Id)).Contains("CS0246")
      .Because("the generator ran and emitted code that names a type nothing defines; that is a compilation "
        + "error about the generated source, which is precisely what this helper exists to return");
  }

  /// <summary>A generator that runs successfully and emits code that does not compile.</summary>
  private sealed class EmitsBrokenCodeGenerator : IIncrementalGenerator {
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
      context.RegisterSourceOutput(
        context.CompilationProvider,
        static (ctx, _) => ctx.AddSource(
          "Broken.g.cs", "namespace TestApp { public class Broken : NoSuchBaseType { } }"));
  }
}
