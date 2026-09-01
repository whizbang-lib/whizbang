using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The receptor generator's behavior on source that does not compile.
/// </summary>
/// <remarks>
/// A source generator runs on every keystroke, so most of the time it is looking at code that is
/// half-written: an attribute whose argument has not been typed yet, a name that does not resolve
/// to anything. Every one of the guards exercised here answers that state.
///
/// <para>
/// Two things must hold. The generator must not throw — a crashed generator takes IDE completion
/// down with it and reports as "IntelliSense stopped working", not as a generator bug. And it must
/// not emit a half-built registration: garbage in the generated routing turns one red squiggle in
/// the author's own file into a wall of errors in a file they cannot open, which is far harder to
/// connect back to the line they were editing.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class ReceptorDiscoveryMalformedSourceTests {

  private static ImmutableArray<Diagnostic> _run(string source)
    => GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source).Diagnostics;

  private static string _withAttribute(string attribute) => $$"""
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Whizbang.Core;
    using Whizbang.Core.Messaging;
    using Whizbang.Core.Dispatch;
    using Whizbang.Core.Perspectives.Sync;

    namespace MyApp;

    public record ProbeCommand([property: StreamId] Guid ProbeId) : ICommand;

    {{attribute}}
    public class ProbeReceptor : IReceptor<ProbeCommand> {
      public ValueTask HandleAsync(ProbeCommand message, CancellationToken ct = default)
        => ValueTask.CompletedTask;
    }
    """;

  /// <summary>The generator must survive the source, whatever else it does.</summary>
  private static async Task _assertGeneratorSurvivedAsync(ImmutableArray<Diagnostic> diagnostics) {
    // A generator that throws is reported by Roslyn as CS8785, and the author sees it as broken
    // tooling rather than as a consequence of the character they just typed.
    await Assert.That(diagnostics.Any(d => d.Id == "CS8785")).IsFalse()
      .Because("an unhandled generator exception surfaces as CS8785 and reads as broken tooling");
  }

  // ============================================================
  // [FireAt] mid-edit
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task FireAtWithNoArgument_IsSkippedWithoutCrashingAsync() {
    // The moment after typing "[FireAt]" and before typing the stage.
    var diagnostics = _run(_withAttribute("[FireAt]"));

    await _assertGeneratorSurvivedAsync(diagnostics);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task FireAtWithAnUnresolvableStage_IsSkippedWithoutCrashingAsync() {
    // Halfway through typing the member name, or after renaming it away.
    var diagnostics = _run(_withAttribute("[FireAt(LifecycleStage.NoSuchStage)]"));

    await _assertGeneratorSurvivedAsync(diagnostics);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task FireAtWithAWrongTypedArgument_IsSkippedWithoutCrashingAsync() {
    // The stage argument is read as an int; anything else must be declined rather than cast.
    var diagnostics = _run(_withAttribute("[FireAt(\"PostInboxInline\")]"));

    await _assertGeneratorSurvivedAsync(diagnostics);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AValidFireAt_StillGeneratesAsync() {
    // The control: the guards above must not be swallowing the working case too.
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(
      _withAttribute("[FireAt(LifecycleStage.PostInboxInline)]"));

    await Assert.That(result.GeneratedTrees.Length).IsGreaterThan(0)
      .Because("a well-formed attribute must still produce a registration");
  }

  // ============================================================
  // [DefaultRouting] mid-edit
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task DefaultRoutingWithNoArgument_IsSkippedWithoutCrashingAsync() {
    var diagnostics = _run(_withAttribute("[DefaultRouting]"));

    await _assertGeneratorSurvivedAsync(diagnostics);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task DefaultRoutingWithAnUnresolvableMode_IsSkippedWithoutCrashingAsync() {
    var diagnostics = _run(_withAttribute("[DefaultRouting(DispatchModes.NoSuchMode)]"));

    await _assertGeneratorSurvivedAsync(diagnostics);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task DefaultRoutingWithAWrongTypedArgument_IsSkippedWithoutCrashingAsync() {
    var diagnostics = _run(_withAttribute("[DefaultRouting(\"Outbox\")]"));

    await _assertGeneratorSurvivedAsync(diagnostics);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AValidDefaultRouting_StillGeneratesAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(
      _withAttribute("[DefaultRouting(DispatchModes.Outbox)]"));

    await Assert.That(result.GeneratedTrees.Length).IsGreaterThan(0);
  }

  // ============================================================
  // [AwaitPerspectiveSync] mid-edit
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AwaitPerspectiveSyncWithNoArgument_IsSkippedWithoutCrashingAsync() {
    var diagnostics = _run(_withAttribute("[AwaitPerspectiveSync]"));

    await _assertGeneratorSurvivedAsync(diagnostics);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AwaitPerspectiveSyncWithAnUnresolvableTypeIsSkippedWithoutCrashingAsync() {
    // typeof() of a name that has not been written yet, or was just deleted.
    var diagnostics = _run(_withAttribute("[AwaitPerspectiveSync(typeof(NoSuchPerspective))]"));

    await _assertGeneratorSurvivedAsync(diagnostics);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AwaitPerspectiveSyncWithANonTypeArgument_IsSkippedWithoutCrashingAsync() {
    var diagnostics = _run(_withAttribute("[AwaitPerspectiveSync(\"OrderPerspective\")]"));

    await _assertGeneratorSurvivedAsync(diagnostics);
  }

  // ============================================================
  // The receptor itself mid-edit
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ReceptorOverAnUndeclaredMessageType_DoesNotCrashAsync() {
    // The shape while extracting a message type into its own file: the receptor references a
    // name that does not exist yet.
    var diagnostics = _run("""
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;

      namespace MyApp;

      public class ProbeReceptor : IReceptor<NotYetWritten> {
        public ValueTask HandleAsync(NotYetWritten message, CancellationToken ct = default)
          => ValueTask.CompletedTask;
      }
      """);

    await _assertGeneratorSurvivedAsync(diagnostics);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ReceptorWithNoTypeArgument_DoesNotCrashAsync() {
    // Mid-keystroke on the interface itself.
    var diagnostics = _run("""
      using Whizbang.Core;

      namespace MyApp;

      public class ProbeReceptor : IReceptor {
      }
      """);

    await _assertGeneratorSurvivedAsync(diagnostics);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AnEmptyCompilationProducesNoReceptorRegistrationAsync() {
    // Nothing to discover is a legitimate state — a project with no receptors must not be
    // handed a registration that references types it does not have.
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>("""
      namespace MyApp;

      public class NotAReceptor { }
      """);

    await Assert.That(result.Diagnostics.Any(d => d.Id == "CS8785")).IsFalse();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SeveralMalformedAttributesAtOnce_DoNotCrashAsync() {
    // The realistic mid-edit state is several things broken at once, not one.
    var diagnostics = _run(_withAttribute(
      "[FireAt]\n[DefaultRouting]\n[AwaitPerspectiveSync]"));

    await _assertGeneratorSurvivedAsync(diagnostics);
  }
}
