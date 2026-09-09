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
///
/// <para>
/// The second half is what "skipped" in these test names means, and it is checked by comparing the
/// generated output against <see cref="_noAttributeBaseline"/> — what the generator emits for the
/// very same receptor carrying no attribute at all. Declining a malformed attribute has to be
/// indistinguishable from never having seen one; asserting only that the generator returned would
/// let a half-parsed attribute through unnoticed.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class ReceptorDiscoveryMalformedSourceTests {

  private static GeneratorDriverRunResult _runFull(string source)
    => GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

  /// <summary>The named generated file, or a failure that says which one went missing.</summary>
  private static string _emitted(GeneratorDriverRunResult result, string fileName)
    => GeneratorTestHelper.GetGeneratedSource(result, fileName)
       ?? throw new InvalidOperationException($"the generator emitted no {fileName}");

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

  /// <summary>
  /// The output for the same receptor with no attribute on it — the shape a declined attribute has
  /// to fall back to. Computed once; the generator is deterministic for a given compilation.
  /// </summary>
  private static readonly Lazy<(string Registry, string Dispatcher)> _noAttributeBaseline =
    new(() => {
      var result = _runFull(_withAttribute(""));
      return (_emitted(result, "ReceptorRegistry.g.cs"), _emitted(result, "Dispatcher.g.cs"));
    });

  /// <summary>The generator must survive the source, whatever else it does.</summary>
  private static async Task _assertGeneratorSurvivedAsync(ImmutableArray<Diagnostic> diagnostics) {
    // A generator that throws is reported by Roslyn as CS8785, and the author sees it as broken
    // tooling rather than as a consequence of the character they just typed.
    await Assert.That(diagnostics.Any(d => d.Id == "CS8785")).IsFalse()
      .Because("an unhandled generator exception surfaces as CS8785 and reads as broken tooling");
  }

  /// <summary>
  /// Runs the receptor with <paramref name="attribute"/> applied and asserts the generator both
  /// survived and declined the attribute outright — the emitted registry and dispatcher must match
  /// the no-attribute baseline byte for byte.
  /// </summary>
  private static async Task<GeneratorDriverRunResult> _assertAttributeWasSkippedAsync(string attribute) {
    var result = _runFull(_withAttribute(attribute));
    await _assertGeneratorSurvivedAsync(result.Diagnostics);

    await Assert.That(_emitted(result, "ReceptorRegistry.g.cs")).IsEqualTo(_noAttributeBaseline.Value.Registry)
      .Because("a malformed attribute must leave the registration identical to one written without it — a partial registration is the wall of errors this guard exists to prevent");
    await Assert.That(_emitted(result, "Dispatcher.g.cs")).IsEqualTo(_noAttributeBaseline.Value.Dispatcher)
      .Because("the dispatcher must not pick up routing or sync behavior from an attribute the generator could not read");
    return result;
  }

  // ============================================================
  // [FireAt] mid-edit
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task FireAtWithNoArgument_IsSkippedWithoutCrashingAsync() {
    // The moment after typing "[FireAt]" and before typing the stage.
    var result = await _assertAttributeWasSkippedAsync("[FireAt]");

    // Skipped means "falls back to the default stages", not "the receptor disappears" — a receptor
    // dropped mid-keystroke would stop firing everywhere until the line is finished.
    await Assert.That(_emitted(result, "ReceptorRegistry.g.cs"))
      .Contains("global::Whizbang.Core.Messaging.LifecycleStage.LocalImmediateDetached")
      .Because("an unreadable [FireAt] leaves the receptor registered at the default stages");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task FireAtWithAnUnresolvableStage_IsSkippedWithoutCrashingAsync() {
    // Halfway through typing the member name, or after renaming it away.
    var result = await _assertAttributeWasSkippedAsync("[FireAt(LifecycleStage.NoSuchStage)]");

    await Assert.That(_emitted(result, "ReceptorRegistry.g.cs")).DoesNotContain("NoSuchStage")
      .Because("the name that does not resolve must not be copied into the generated stage check");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task FireAtWithAWrongTypedArgument_IsSkippedWithoutCrashingAsync() {
    // The stage argument is read as an int; anything else must be declined rather than cast.
    var result = await _assertAttributeWasSkippedAsync("[FireAt(\"PostInboxInline\")]");

    // The string spells a REAL stage. Guessing at what the author meant would silently move the
    // receptor off the default stages on the strength of source that does not compile.
    await Assert.That(_emitted(result, "ReceptorRegistry.g.cs")).DoesNotContain("PostInboxInline")
      .Because("a string argument is not a stage, however much it looks like one");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AValidFireAt_StillGeneratesAsync() {
    // The control: the guards above must not be swallowing the working case too.
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(
      _withAttribute("[FireAt(LifecycleStage.PostInboxInline)]"));

    await Assert.That(result.GeneratedTrees.Length).IsGreaterThan(0)
      .Because("a well-formed attribute must still produce a registration");
    await Assert.That(_emitted(result, "ReceptorRegistry.g.cs"))
      .Contains("global::Whizbang.Core.Messaging.LifecycleStage.PostInboxInline")
      .Because("and the stage it names must reach the generated registration — otherwise the DoesNotContain assertions above would pass on a generator that reads no stage at all");
  }

  // ============================================================
  // [DefaultRouting] mid-edit
  // ============================================================

  /// <summary>The generated marker for "this message type has a receptor-declared routing override".</summary>
  private const string ROUTING_OVERRIDE = "return global::Whizbang.Core.Dispatch.DispatchModes.";

  [Test]
  [RequiresAssemblyFiles()]
  public async Task DefaultRoutingWithNoArgument_IsSkippedWithoutCrashingAsync() {
    var result = await _assertAttributeWasSkippedAsync("[DefaultRouting]");

    await Assert.That(_emitted(result, "Dispatcher.g.cs")).DoesNotContain(ROUTING_OVERRIDE)
      .Because("an argument-less [DefaultRouting] declares no mode, so GetReceptorDefaultRouting must keep returning null and leave the send path on its normal routing");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task DefaultRoutingWithAnUnresolvableMode_IsSkippedWithoutCrashingAsync() {
    var result = await _assertAttributeWasSkippedAsync("[DefaultRouting(DispatchModes.NoSuchMode)]");

    await Assert.That(_emitted(result, "Dispatcher.g.cs")).DoesNotContain("NoSuchMode");
    await Assert.That(_emitted(result, "Dispatcher.g.cs")).DoesNotContain(ROUTING_OVERRIDE)
      .Because("an unresolvable mode must not be guessed at — routing a command to the wrong transport is silent and permanent");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task DefaultRoutingWithAWrongTypedArgument_IsSkippedWithoutCrashingAsync() {
    var result = await _assertAttributeWasSkippedAsync("[DefaultRouting(\"Outbox\")]");

    await Assert.That(_emitted(result, "Dispatcher.g.cs")).DoesNotContain(ROUTING_OVERRIDE)
      .Because("the string names a real mode, and honoring it would let source that does not compile change where a message is published");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AValidDefaultRouting_StillGeneratesAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(
      _withAttribute("[DefaultRouting(DispatchModes.Outbox)]"));

    await Assert.That(result.GeneratedTrees.Length).IsGreaterThan(0);
    await Assert.That(_emitted(result, "Dispatcher.g.cs"))
      .Contains("return global::Whizbang.Core.Dispatch.DispatchModes.Outbox;")
      .Because("the control for the three DoesNotContain assertions above: a readable mode does reach GetReceptorDefaultRouting");
  }

  // ============================================================
  // [AwaitPerspectiveSync] mid-edit
  // ============================================================

  /// <summary>What the dispatcher emits in place of a sync wait when the receptor declared none.</summary>
  private const string NO_SYNC_MARKER = "// No [AwaitPerspectiveSync] attributes - skip sync checking";

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AwaitPerspectiveSyncWithNoArgument_IsSkippedWithoutCrashingAsync() {
    var result = await _assertAttributeWasSkippedAsync("[AwaitPerspectiveSync]");

    await Assert.That(_emitted(result, "Dispatcher.g.cs")).Contains(NO_SYNC_MARKER)
      .Because("no perspective type was named, so the invoker must carry no wait at all — a half-built wait blocks the message for its whole timeout and then throws");
    await Assert.That(_emitted(result, "ReceptorRegistry.g.cs")).Contains("SyncAttributes: null");
  }

  /// <summary>
  /// Characterizes a DEFECT, deliberately: unlike every sibling guard in this file, an
  /// <c>[AwaitPerspectiveSync(typeof(Unresolvable))]</c> is NOT declined.
  /// </summary>
  /// <remarks>
  /// Roslyn hands the generator an error-type symbol for <c>typeof(NoSuchPerspective)</c>, and
  /// <c>_parseSingleSyncAttribute</c> accepts it where <c>_tryExtractFireAtStage</c> and
  /// <c>_extractDefaultRouting</c> both reject their unresolvable counterparts. The unqualified
  /// name is then copied verbatim into <c>ReceptorRegistry.g.cs</c>, which is exactly the
  /// half-built registration this file's remarks say must never be emitted: the author gets
  /// CS0246 inside a generated file they cannot open, on top of the CS0246 in their own.
  ///
  /// <para>
  /// The test is named for what the generator does rather than for what it should do, and asserts
  /// it, so the behavior is pinned and visible. When the parse guard is tightened to reject error
  /// types, this test fails and is replaced by a call to
  /// <c>_assertAttributeWasSkippedAsync("[AwaitPerspectiveSync(typeof(NoSuchPerspective))]")</c> —
  /// the same assertion its siblings already use.
  /// </para>
  /// </remarks>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task AwaitPerspectiveSyncWithAnUnresolvableType_LeaksTheNameIntoTheRegistryAsync() {
    // typeof() of a name that has not been written yet, or was just deleted.
    var result = _runFull(_withAttribute("[AwaitPerspectiveSync(typeof(NoSuchPerspective))]"));

    await _assertGeneratorSurvivedAsync(result.Diagnostics);

    await Assert.That(_emitted(result, "ReceptorRegistry.g.cs")).Contains("typeof(NoSuchPerspective)")
      .Because("DEFECT: the unresolved perspective type is emitted into the generated registry instead of being declined the way an unresolvable [FireAt] stage is");
    await Assert.That(_emitted(result, "ReceptorRegistry.g.cs")).IsNotEqualTo(_noAttributeBaseline.Value.Registry)
      .Because("stating the same defect the other way round: the output is NOT the no-attribute output, so the attribute was not skipped");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AwaitPerspectiveSyncWithANonTypeArgument_IsSkippedWithoutCrashingAsync() {
    var result = await _assertAttributeWasSkippedAsync("[AwaitPerspectiveSync(\"OrderPerspective\")]");

    await Assert.That(_emitted(result, "Dispatcher.g.cs")).Contains(NO_SYNC_MARKER)
      .Because("a string is not a perspective type, so no wait may be generated from it");
    await Assert.That(_emitted(result, "ReceptorRegistry.g.cs")).DoesNotContain("OrderPerspective");
  }

  // ============================================================
  // The receptor itself mid-edit
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ReceptorOverAnUndeclaredMessageType_DoesNotCrashAsync() {
    // The shape while extracting a message type into its own file: the receptor references a
    // name that does not exist yet.
    var result = _runFull("""
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;

      namespace MyApp;

      public class ProbeReceptor : IReceptor<NotYetWritten> {
        public ValueTask HandleAsync(NotYetWritten message, CancellationToken ct = default)
          => ValueTask.CompletedTask;
      }
      """);

    await _assertGeneratorSurvivedAsync(result.Diagnostics);

    // Surviving is not enough on its own: the generator must still emit the whole set. Bailing out
    // of a compilation that has one unresolved name would delete GeneratedDispatcher and
    // GeneratedReceptorRegistry, and every call site in the project would go red at once — for the
    // duration of one half-typed type name.
    var emitted = result.GeneratedTrees.Select(t => Path.GetFileName(t.FilePath)).ToArray();
    await Assert.That(emitted).Contains("Dispatcher.g.cs");
    await Assert.That(emitted).Contains("ReceptorRegistry.g.cs");
    await Assert.That(emitted).Contains("DispatcherRegistrations.g.cs")
      .Because("one unresolved message type must not cost the project its generated dispatcher");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ReceptorWithNoTypeArgument_DoesNotCrashAsync() {
    // Mid-keystroke on the interface itself.
    var result = _runFull("""
      using Whizbang.Core;

      namespace MyApp;

      public class ProbeReceptor : IReceptor {
      }
      """);

    await _assertGeneratorSurvivedAsync(result.Diagnostics);

    // Non-generic IReceptor names no message, so there is nothing to route: the class must be
    // passed over rather than registered against a missing message type.
    await Assert.That(_emitted(result, "ReceptorRegistry.g.cs")).DoesNotContain("ProbeReceptor")
      .Because("a receptor with no message type cannot be registered — inventing an entry for it would emit a registration keyed on nothing");
    await Assert.That(result.Diagnostics.Any(d => d.Id == "WHIZ002")).IsTrue()
      .Because("the generator reports the compilation as having no discoverable receptors, which is the honest reading of this source");
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
    await Assert.That(_emitted(result, "ReceptorRegistry.g.cs")).DoesNotContain("NotAReceptor")
      .Because("a class that implements no receptor interface must not appear in the routing");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SeveralMalformedAttributesAtOnce_DoNotCrashAsync() {
    // The realistic mid-edit state is several things broken at once, not one.
    var result = await _assertAttributeWasSkippedAsync(
      "[FireAt]\n[DefaultRouting]\n[AwaitPerspectiveSync]");

    // Each guard has to hold independently — one attribute being declined must not be what
    // happens to make the others look declined.
    await Assert.That(_emitted(result, "Dispatcher.g.cs")).DoesNotContain(ROUTING_OVERRIDE);
    await Assert.That(_emitted(result, "Dispatcher.g.cs")).Contains(NO_SYNC_MARKER);
    await Assert.That(_emitted(result, "ReceptorRegistry.g.cs"))
      .Contains("global::Whizbang.Core.Messaging.LifecycleStage.LocalImmediateDetached")
      .Because("and the receptor still fires at the default stages while the author finishes typing");
  }
}
