using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for <see cref="EventNamespaceRegistryGenerator"/> targeting: a perspective
/// event type that fails IEvent resolution, an event receptor whose event lives in the global
/// namespace, and the three ordinal-sort call sites that <c>EventNamespaceRegistryGeneratorTests.cs</c>
/// never exercises with more than one namespace per set (so a real element-to-element comparison,
/// rather than mere key extraction, never actually ran).
/// </summary>
[Category("SourceGenerators")]
public class EventNamespaceRegistryGeneratorCoverageTests {

  /// <summary>
  /// The shape while extracting an event type into its own file: the perspective references a name
  /// that does not exist yet. <c>ExtractEventTypes</c> hands back whatever type argument is there —
  /// error type included — so the IEvent check downstream is what actually decides membership. It must
  /// reject the unresolved type (rather than crash, or add a namespace for it), and since it is the
  /// perspective's only event candidate, the perspective must end up contributing no namespace at all.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_PerspectiveEventTypeFailsResolution_ContributesNoNamespaceAsync() {
    const string source = """
      using Whizbang.Core.Perspectives;

      namespace MyApp.Perspectives;

      public record ProbeModel {
        public System.Guid Id { get; set; }
      }

      public class ProbePerspective : IPerspectiveFor<ProbeModel, NotYetWritten> {
        public ProbeModel Apply(ProbeModel currentData, NotYetWritten @event) => currentData;
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<EventNamespaceRegistryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "EventNamespaceSource.g.cs");

    await Assert.That(code).IsNotNull()
      .Because("the registry fragment must still be emitted even when a perspective's only event type fails to resolve");
    var perspectiveRegion = _extractRegion(code!, "_perspectiveNamespaces");
    await Assert.That(perspectiveRegion).DoesNotContain("\"")
      .Because("an event type that fails IEvent resolution is not a usable event — the perspective's namespace set must stay empty rather than register a bogus entry");
  }

  /// <summary>
  /// An event receptor whose event type is declared in the global namespace (no namespace at all) has
  /// no namespace string to contribute. It must be skipped rather than register an empty-string
  /// "namespace", which would make every message in the true global namespace look like a match for
  /// event-subscription discovery.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EventReceptorForGlobalNamespaceEvent_ContributesNoNamespaceAsync() {
    const string source = """
      using Whizbang.Core;
      using System.Threading;
      using System.Threading.Tasks;

      public record GlobalScopedEvent : IEvent;

      namespace MyApp;

      public class GlobalEventReceptor : IReceptor<GlobalScopedEvent> {
        public ValueTask HandleAsync(GlobalScopedEvent message, CancellationToken ct = default) => ValueTask.CompletedTask;
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<EventNamespaceRegistryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "EventNamespaceSource.g.cs");

    await Assert.That(code).IsNotNull();
    var receptorRegion = _extractRegion(code!, "_receptorNamespaces");
    await Assert.That(receptorRegion).DoesNotContain("\"")
      .Because("a global-namespace event has no namespace to route on — the receptor namespace set must stay empty rather than register an empty-string namespace");
  }

  /// <summary>
  /// Two perspectives and two event receptors, each pair in namespaces declared in reverse-alphabetical
  /// order in source. If the generator's output were unsorted (or accidentally preserved declaration
  /// order), the emitted file's byte content would vary run-to-run for the same logical set of
  /// namespaces — defeating deterministic/reproducible builds and making every rebuild look like a
  /// source-control diff. This exercises all three ordinal-sort call sites (perspective set, receptor
  /// set, and their union) with two distinct entries each, so the sort actually compares elements
  /// instead of merely extracting a single key.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MultipleDistinctNamespaces_SortsEachSetAndTheirUnionAsync() {
    const string source = """
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using System;
      using System.Threading;
      using System.Threading.Tasks;

      namespace Zeta.Perspectives {
        public record ZetaEvent : IEvent { [StreamId] public Guid Id { get; init; } }
        public record ZetaModel { [StreamId] public Guid Id { get; init; } }
        public class ZetaPerspective : IPerspectiveFor<ZetaModel, ZetaEvent> {
          public ZetaModel Apply(ZetaModel currentData, ZetaEvent @event) => currentData;
        }
      }

      namespace Alpha.Perspectives {
        public record AlphaEvent : IEvent { [StreamId] public Guid Id { get; init; } }
        public record AlphaModel { [StreamId] public Guid Id { get; init; } }
        public class AlphaPerspective : IPerspectiveFor<AlphaModel, AlphaEvent> {
          public AlphaModel Apply(AlphaModel currentData, AlphaEvent @event) => currentData;
        }
      }

      namespace Zeta.Receptors {
        public record ZetaReceptorEvent : IEvent { [StreamId] public Guid Id { get; init; } }
        public class ZetaReceptor : IReceptor<ZetaReceptorEvent> {
          public ValueTask HandleAsync(ZetaReceptorEvent message, CancellationToken ct = default) => ValueTask.CompletedTask;
        }
      }

      namespace Alpha.Receptors {
        public record AlphaReceptorEvent : IEvent { [StreamId] public Guid Id { get; init; } }
        public class AlphaReceptor : IReceptor<AlphaReceptorEvent> {
          public ValueTask HandleAsync(AlphaReceptorEvent message, CancellationToken ct = default) => ValueTask.CompletedTask;
        }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<EventNamespaceRegistryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "EventNamespaceSource.g.cs");
    await Assert.That(code).IsNotNull();

    var perspectiveRegion = _extractRegion(code!, "_perspectiveNamespaces");
    var alphaPerspectiveIndex = perspectiveRegion.IndexOf("alpha.perspectives", StringComparison.Ordinal);
    var zetaPerspectiveIndex = perspectiveRegion.IndexOf("zeta.perspectives", StringComparison.Ordinal);
    await Assert.That(alphaPerspectiveIndex).IsGreaterThanOrEqualTo(0);
    await Assert.That(zetaPerspectiveIndex).IsGreaterThan(alphaPerspectiveIndex)
      .Because("perspective namespaces must be ordinal-sorted regardless of declaration order — Zeta was declared first in source");

    var receptorRegion = _extractRegion(code!, "_receptorNamespaces");
    var alphaReceptorIndex = receptorRegion.IndexOf("alpha.receptors", StringComparison.Ordinal);
    var zetaReceptorIndex = receptorRegion.IndexOf("zeta.receptors", StringComparison.Ordinal);
    await Assert.That(alphaReceptorIndex).IsGreaterThanOrEqualTo(0);
    await Assert.That(zetaReceptorIndex).IsGreaterThan(alphaReceptorIndex)
      .Because("receptor namespaces must likewise be ordinal-sorted regardless of declaration order");

    var allRegion = _extractRegion(code!, "_allNamespaces");
    var allAlphaPerspectiveIndex = allRegion.IndexOf("alpha.perspectives", StringComparison.Ordinal);
    var allAlphaReceptorIndex = allRegion.IndexOf("alpha.receptors", StringComparison.Ordinal);
    var allZetaPerspectiveIndex = allRegion.IndexOf("zeta.perspectives", StringComparison.Ordinal);
    var allZetaReceptorIndex = allRegion.IndexOf("zeta.receptors", StringComparison.Ordinal);
    await Assert.That(allAlphaPerspectiveIndex).IsLessThan(allAlphaReceptorIndex)
      .Because("the unioned set must be re-sorted, not just perspective-namespaces-then-receptor-namespaces appended in that order");
    await Assert.That(allAlphaReceptorIndex).IsLessThan(allZetaPerspectiveIndex);
    await Assert.That(allZetaPerspectiveIndex).IsLessThan(allZetaReceptorIndex);
  }

  /// <summary>Isolates the braced initializer for one static field (e.g. "_perspectiveNamespaces") so
  /// assertions are scoped to that field's own entries instead of the whole generated file.</summary>
  private static string _extractRegion(string source, string fieldName) {
    var fieldStart = source.IndexOf(fieldName, StringComparison.Ordinal);
    if (fieldStart < 0) {
      return string.Empty;
    }
    var braceStart = source.IndexOf('{', fieldStart);
    if (braceStart < 0) {
      return string.Empty;
    }
    var depth = 0;
    for (var i = braceStart; i < source.Length; i++) {
      if (source[i] == '{') {
        depth++;
      } else if (source[i] == '}') {
        depth--;
        if (depth == 0) {
          return source[braceStart..(i + 1)];
        }
      }
    }
    return source[braceStart..];
  }
}
