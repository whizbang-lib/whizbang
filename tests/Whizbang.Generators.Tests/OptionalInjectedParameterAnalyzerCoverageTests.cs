using System.Diagnostics.CodeAnalysis;
using System.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Analyzers;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for <see cref="OptionalInjectedParameterAnalyzer"/>, complementing
/// <c>tests/Whizbang.Generators.Tests/Analyzers/OptionalInjectedParameterAnalyzerTests.cs</c>. Every
/// existing test declares a class whose only user-written method is the constructor under test, so
/// <c>_analyze</c>'s two early-return guards — "this symbol isn't a constructor" and "this
/// constructor isn't public/internal" — have never actually run. These tests exercise both.
/// </summary>
[Category("Analyzers")]
public class OptionalInjectedParameterAnalyzerCoverageTests {

  /// <summary>
  /// The rule targets constructor parameters only. If the "not a constructor" guard regressed, an
  /// ordinary method's optional interface parameter would also get flagged — the diagnostic message
  /// talks about construction sites dropping a dependency, which is nonsense for a plain method call
  /// a DI container never resolves, and would bury the real WHIZ501 signal under noise.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task NonConstructorMethodWithOptionalInterfaceParameterIsNotReportedAsync() {
    const string source = """
      namespace App;
      public interface IClock { }
      public sealed class Worker {
        public void Configure(IClock? clock = null) { }
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<OptionalInjectedParameterAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ501")).IsEmpty()
      .Because("_analyze must bail out on any non-constructor method symbol before it ever inspects parameters");
  }

  /// <summary>
  /// A private (or protected) constructor is never invoked by a DI container — it exists for a
  /// factory or singleton-accessor pattern. If the accessibility guard regressed, the analyzer would
  /// flag that internal-only constructor's optional interface parameter with a message about a
  /// construction site silently dropping a dependency, even though no container-driven construction
  /// site can reach it at all.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task PrivateConstructorWithOptionalInterfaceParameterIsNotReportedAsync() {
    const string source = """
      namespace App;
      public interface IClock { }
      public sealed class Worker {
        private Worker(IClock? clock = null) { }
        public static Worker Create() => new Worker();
      }
      """;

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<OptionalInjectedParameterAnalyzer>(source);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ501")).IsEmpty()
      .Because("_analyze must bail out on a constructor whose declared accessibility is neither public nor internal");
  }
}
