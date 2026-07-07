using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for EventNamespaceRegistryGenerator.
/// Verifies event namespace routing includes IPerspectiveWithActionsFor events.
/// </summary>
[Category("SourceGenerators")]
public class EventNamespaceRegistryGeneratorTests {

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_IPerspectiveWithActionsFor_IncludesEventNamespaceAsync() {
    // Arrange — EXACT same source as passing PerspectiveRunnerRegistryGenerator test
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestApp.Events {
  public record DeletedEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record Model {
    [StreamId]
    public Guid Id { get; init; }
  }

  public class PurgeOnlyPerspective : IPerspectiveWithActionsFor<Model, DeletedEvent> {
    public ApplyResult<Model> Apply(Model current, DeletedEvent @event)
        => ApplyResult<Model>.Purge();
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<EventNamespaceRegistryGenerator>(source);

    // Assert — Event namespace must be included for routing
    var code = GeneratorTestHelper.GetGeneratedSource(result, "EventNamespaceSource.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains("testapp.events")
      .Because("IPerspectiveWithActionsFor event namespaces must be included in routing registry (lowercased)");
  }

  /// <summary>
  /// A receptor handling an event type (<c>IReceptor&lt;TEvent&gt;</c> where <c>TEvent : IEvent</c>)
  /// must contribute its event's namespace to the receptor namespace set (and the union set).
  /// Covers the receptor-extraction happy path and the receptor-namespace emission loop.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_EventReceptor_IncludesReceptorNamespaceAsync() {
    // Arrange — an event receptor whose event lives in a distinct namespace.
    const string source = @"
using Whizbang.Core;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Receptors.Domain {
  public record ShipmentDispatched : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public class ShipmentReceptor : IReceptor<ShipmentDispatched> {
    public ValueTask HandleAsync(ShipmentDispatched message, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<EventNamespaceRegistryGenerator>(source);

    // Assert — receptor namespace included and rendered as a receptor namespace field.
    var code = GeneratorTestHelper.GetGeneratedSource(result, "EventNamespaceSource.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("receptors.domain")
      .Because("IReceptor<TEvent> event namespaces must be included in the routing registry (lowercased)");
    await Assert.That(code).Contains("1 receptor namespace(s)")
      .Because("the summary comment reflects the discovered receptor namespace count");
  }

  /// <summary>
  /// A receptor whose message type is NOT an event (does not implement IEvent) must be ignored —
  /// the receptor extraction returns null and no receptor namespace is contributed.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NonEventReceptor_ExcludedAsync() {
    // Arrange — a receptor over a plain command (not an IEvent).
    const string source = @"
using Whizbang.Core;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Commands.Domain {
  public record CreateWidget {
    public Guid Id { get; init; }
  }

  public class WidgetReceptor : IReceptor<CreateWidget> {
    public ValueTask HandleAsync(CreateWidget message, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<EventNamespaceRegistryGenerator>(source);

    // Assert — no receptor namespace discovered.
    var code = GeneratorTestHelper.GetGeneratedSource(result, "EventNamespaceSource.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("0 receptor namespace(s)")
      .Because("a receptor over a non-event message contributes no event namespace");
    await Assert.That(code).DoesNotContain("commands.domain")
      .Because("non-event receptor namespaces must not enter the routing registry");
  }

  /// <summary>
  /// An open-generic receptor (unbound type parameter) must be skipped — the
  /// IsGenericType/TypeParameters guard returns null before any interface scan.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_OpenGenericReceptor_SkippedAsync() {
    // Arrange — an open-generic receptor definition; T is unbound.
    const string source = @"
using Whizbang.Core;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Generic.Domain {
  public record OpenEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public class GenericReceptor<T> : IReceptor<OpenEvent> where T : class {
    public ValueTask HandleAsync(OpenEvent message, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<EventNamespaceRegistryGenerator>(source);

    // Assert — the open-generic class is skipped by the generic-type guard.
    var code = GeneratorTestHelper.GetGeneratedSource(result, "EventNamespaceSource.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("0 receptor namespace(s)")
      .Because("open-generic receptor definitions are skipped before namespace extraction");
  }

  /// <summary>
  /// When both a perspective and a receptor contribute the SAME event namespace, the union set
  /// deduplicates case-insensitively — the perspective and receptor namespace sets each carry the
  /// namespace and the combined _allNamespaces set carries it exactly once.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_PerspectiveAndReceptorSameNamespace_DeduplicatedInUnionAsync() {
    // Arrange — a perspective and a receptor over events in the same namespace.
    const string source = @"
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Shared.Events {
  public record OrderPlaced : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record OrderShipped : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public class OrderModel {
    [StreamId]
    public Guid Id { get; init; }
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderPlaced> {
    public OrderModel Apply(OrderModel current, OrderPlaced @event) => current;
  }

  public class OrderShippedReceptor : IReceptor<OrderShipped> {
    public ValueTask HandleAsync(OrderShipped message, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
  }
}";

    // Act
    var result = GeneratorTestHelper.RunGenerator<EventNamespaceRegistryGenerator>(source);

    // Assert — namespace present, counted once in perspective + once in receptor set.
    var code = GeneratorTestHelper.GetGeneratedSource(result, "EventNamespaceSource.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("shared.events")
      .Because("the shared namespace must appear in the registry");
    await Assert.That(code).Contains("1 perspective namespace(s) and 1 receptor namespace(s)")
      .Because("each side contributes the namespace exactly once");

    // The union (_allNamespaces) must carry the namespace exactly once despite two contributors.
    var allNamespacesSection = code!.Substring(code.IndexOf("_allNamespaces", StringComparison.Ordinal));
    var occurrences = allNamespacesSection.Split(["\"shared.events\""], StringSplitOptions.None).Length - 1;
    await Assert.That(occurrences).IsEqualTo(1)
      .Because("_allNamespaces deduplicates the namespace shared by perspective and receptor");
  }

}
