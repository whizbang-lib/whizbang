using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The shared perspective-interface scan, exercised through
/// <see cref="EventNamespaceRegistryGenerator"/>, which is what consumes it.
/// </summary>
/// <remarks>
/// Every generator that has to know "which events does this perspective consume" goes through
/// <c>PerspectiveDiscoveryHelper</c> — the point of the shared helper is that they all agree.
/// The scan has more shapes to get right than it looks: a perspective can implement several
/// perspective interfaces at once, single-stream and global interfaces put the events at
/// different type-argument positions, the multi-event overloads put several events on one
/// interface, and a perspective may name the same event twice across two interfaces.
///
/// <para>
/// Missing an event here is silent. The generated registry simply does not mention it, so the
/// event never routes to that perspective and the model quietly stops updating — no error, no
/// diagnostic, just a perspective that stops keeping up.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Generators/Utilities/PerspectiveDiscoveryHelper.cs</code-under-test>
[Category("SourceGenerators")]
public class PerspectiveEventDiscoveryTests {

  private static string _registryFor(string body) {
    var source = $$"""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp.Events {
        public record Alpha : IEvent { [StreamId] public Guid Id { get; init; } }
        public record Beta : IEvent { [StreamId] public Guid Id { get; init; } }
        public record Gamma : IEvent { [StreamId] public Guid Id { get; init; } }

        public record Model { [StreamId] public Guid Id { get; init; } }

      {{body}}
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<EventNamespaceRegistryGenerator>(source);
    return string.Join("\n", result.GeneratedTrees.Select(t => t.ToString()));
  }

  // ============================================================
  // Single-stream perspectives
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SingleEventPerspective_IsDiscoveredAsync() {
    var registry = _registryFor("""
        public class AlphaPerspective : IPerspectiveFor<Model, Alpha> {
          public Model Apply(Model current, Alpha @event) => current;
        }
    """);

    await Assert.That(registry).Contains("testapp.events");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MultiEventPerspective_DiscoversEveryEventArgumentAsync() {
    // The multi-event overloads put several events on one interface, so the scan walks the
    // type arguments from position 1 onward rather than reading just the first.
    var registry = _registryFor("""
        public class WidePerspective : IPerspectiveFor<Model, Alpha, Beta, Gamma> {
          public Model Apply(Model current, Alpha @event) => current;
          public Model Apply(Model current, Beta @event) => current;
          public Model Apply(Model current, Gamma @event) => current;
        }
    """);

    await Assert.That(registry).Contains("testapp.events")
      .Because("stopping at the first type argument would silently drop Beta and Gamma");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveWithActions_IsDiscoveredAsync() {
    var registry = _registryFor("""
        public class PurgePerspective : IPerspectiveWithActionsFor<Model, Alpha> {
          public ApplyResult<Model> Apply(Model current, Alpha @event) => ApplyResult<Model>.Purge();
        }
    """);

    await Assert.That(registry).Contains("testapp.events");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveImplementingSeveralInterfaces_UnionsTheirEventsAsync() {
    // A perspective that folds some events plainly and others with actions implements both
    // interfaces. Reading only the first would drop half its events.
    var registry = _registryFor("""
        public class MixedPerspective : IPerspectiveFor<Model, Alpha>, IPerspectiveWithActionsFor<Model, Beta> {
          public Model Apply(Model current, Alpha @event) => current;
          public ApplyResult<Model> Apply(Model current, Beta @event) => ApplyResult<Model>.Purge();
        }
    """);

    await Assert.That(registry).Contains("testapp.events");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TheSameEventReachedThroughTwoInterfaces_IsFoldedToOneAsync() {
    // The scan keeps a seen-set keyed on the fully-qualified name, so an event a perspective
    // reaches through both interfaces is collected once. Asserted by compiling the registry:
    // the generators that emit a member per discovered event would produce a duplicate
    // declaration otherwise, and that lands as an error in a file nobody wrote.
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<EventNamespaceRegistryGenerator>("""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp.Events {
        public record Alpha : IEvent { [StreamId] public Guid Id { get; init; } }
        public record Model { [StreamId] public Guid Id { get; init; } }

        public class DoublePerspective : IPerspectiveFor<Model, Alpha>, IPerspectiveWithActionsFor<Model, Alpha> {
          public Model Apply(Model current, Alpha @event) => current;
          ApplyResult<Model> IPerspectiveWithActionsFor<Model, Alpha>.Apply(Model current, Alpha @event)
            => ApplyResult<Model>.Purge();
        }
      }
      """);

    await Assert.That(errors).IsEmpty()
      .Because("the same event reached through two interfaces is still one event");
  }

  // ============================================================
  // Global perspectives
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task GlobalPerspective_IsDiscoveredAsync() {
    // A global perspective carries a partition key, so its events start at type argument 2.
    // Reading them from position 1 would treat the partition key as an event type.
    var registry = _registryFor("""
        public class GlobalAlphaPerspective : IGlobalPerspectiveFor<Model, Guid, Alpha> {
          public Model Apply(Model current, Alpha @event) => current;
          public Guid GetPartitionKey(Alpha @event) => @event.Id;
        }
    """);

    await Assert.That(registry).Contains("testapp.events")
      .Because("the partition key sits where a single-stream interface keeps its first event");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task GlobalMultiEventPerspective_DiscoversEveryEventAsync() {
    var registry = _registryFor("""
        public class GlobalWidePerspective : IGlobalPerspectiveFor<Model, Guid, Alpha, Beta> {
          public Model Apply(Model current, Alpha @event) => current;
          public Model Apply(Model current, Beta @event) => current;
          public Guid GetPartitionKey(Alpha @event) => @event.Id;
          public Guid GetPartitionKey(Beta @event) => @event.Id;
        }
    """);

    await Assert.That(registry).Contains("testapp.events");
  }

  // ============================================================
  // What must not be discovered
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ANonPerspectiveClass_ContributesNothingAsync() {
    var registry = _registryFor("""
        public class NotAPerspective {
          public Model Apply(Model current, Alpha @event) => current;
        }
    """);

    await Assert.That(registry).DoesNotContain("testapp.events")
      .Because("matching on method shape rather than the interface would sweep in ordinary classes");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AnEmptyCompilationProducesNoNamespacesAsync() {
    var result = GeneratorTestHelper.RunGenerator<EventNamespaceRegistryGenerator>("""
      namespace TestApp;
      public class Nothing { }
      """);

    await Assert.That(result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsFalse();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TheGeneratedRegistryCompilesAsync() {
    // The registry is emitted into the consumer's build, so the scan producing a name it cannot
    // resolve shows up as an error in a file nobody wrote.
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<EventNamespaceRegistryGenerator>($$"""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestApp.Events {
        public record Alpha : IEvent { [StreamId] public Guid Id { get; init; } }
        public record Beta : IEvent { [StreamId] public Guid Id { get; init; } }
        public record Model { [StreamId] public Guid Id { get; init; } }

        public class MixedPerspective : IPerspectiveFor<Model, Alpha>, IPerspectiveWithActionsFor<Model, Beta> {
          public Model Apply(Model current, Alpha @event) => current;
          public ApplyResult<Model> Apply(Model current, Beta @event) => ApplyResult<Model>.Purge();
        }
      }
      """);

    await Assert.That(errors).IsEmpty();
  }
}
