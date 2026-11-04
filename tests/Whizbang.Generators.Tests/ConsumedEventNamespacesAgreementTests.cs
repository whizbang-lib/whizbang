// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The event namespaces a service subscribes to cover every event it consumes (#2058). The event-namespace registry
/// decides the transport subscriptions; the consumer registry decides what the receive boundary keeps. Subscriptions
/// are namespace-granular and may cover more than is consumed, but never less: an event consumed in a namespace with
/// no subscription is never delivered. One direction only, by design.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/EventNamespaceRegistryGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/ReceptorRegistryQueryGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public partial class ConsumedEventNamespacesAgreementTests {

  private const string SOURCE = """
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Contracts.Received {
  public record ItemReceived : IEvent { public string Id { get; init; } = string.Empty; }
}

namespace Contracts.Folded {
  public record ItemFolded : IEvent { public string Id { get; init; } = string.Empty; }
}

namespace Contracts.Staged {
  public record ItemStaged : IEvent { public string Id { get; init; } = string.Empty; }
}

namespace MyApp {
  public record ItemModel { public string Id { get; set; } = string.Empty; }

  public class ReceivedReceptor : IReceptor<Contracts.Received.ItemReceived> {
    public ValueTask HandleAsync(Contracts.Received.ItemReceived message, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  [FireAt(LifecycleStage.PostInboxDetached)]
  public class StagedReceptor : IReceptor<Contracts.Staged.ItemStaged> {
    public ValueTask HandleAsync(Contracts.Staged.ItemStaged message, CancellationToken ct = default) => ValueTask.CompletedTask;
  }

  public class ItemProjection : IPerspectiveFor<ItemModel, Contracts.Folded.ItemFolded> {
    public ApplyResult<ItemModel> Apply(ItemModel current, Contracts.Folded.ItemFolded @event)
      => ApplyResult<ItemModel>.Update(new ItemModel { Id = @event.Id });
  }
}
""";

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EveryConsumedEventsNamespace_IsAmongTheSubscribedEventNamespacesAsync() {
    var consumedNamespaces = _consumedEventNamespaces();
    var subscribed = _subscribedNamespaces();

    await Assert.That(consumedNamespaces).IsEquivalentTo(["contracts.received", "contracts.folded", "contracts.staged"],
      TUnit.Assertions.Enums.CollectionOrdering.Any)
      .Because("the fixture consumes one event in each namespace; if this changes, the check below proves less");
    foreach (var ns in consumedNamespaces) {
      await Assert.That(subscribed).Contains(ns)
        .Because($"an event consumed in '{ns}' is delivered only if the service subscribes to that namespace");
    }
  }

  private static List<string> _consumedEventNamespaces() {
    var generated = _generated<ReceptorRegistryQueryGenerator>("WhizbangReceptorRegistryQueryRegistration.g.cs");
    return [.. _consumedEvent().Matches(generated)
      .Select(m => m.Groups[1].Value)
      .Select(name => name[..name.LastIndexOf('.')].ToLowerInvariant())
      .Distinct(StringComparer.Ordinal)];
  }

  private static List<string> _subscribedNamespaces() {
    var generated = _generated<EventNamespaceRegistryGenerator>("EventNamespaceSource.g.cs");
    var start = generated.IndexOf("_allNamespaces", StringComparison.Ordinal);
    var region = generated[start..generated.IndexOf("};", start, StringComparison.Ordinal)];
    return [.. _quoted().Matches(region).Select(m => m.Groups[1].Value)];
  }

  private static string _generated<TGenerator>(string hint) where TGenerator : IIncrementalGenerator, new() {
    var result = GeneratorTestHelper.RunGenerator<TGenerator>(SOURCE);
    return GeneratorTestHelper.GetGeneratedSource(result, hint) ?? throw new InvalidOperationException(
      $"{hint} was not generated: " + string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
  }

  [GeneratedRegex("ConsumedMessageInfo\\(\"([^\",]+), TestAssembly\", true\\)")]
  private static partial Regex _consumedEvent();

  [GeneratedRegex("\"([^\"]+)\"")]
  private static partial Regex _quoted();
}
