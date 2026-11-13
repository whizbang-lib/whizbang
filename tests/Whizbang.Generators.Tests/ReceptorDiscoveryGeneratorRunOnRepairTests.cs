// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// #2273: <c>[RunOnRepair]</c> is discovered at compile time and stamped on every registry entry of the receptor, for
/// each receptor shape (async or sync, void or with a response), so the invoker can let it run for a repaired event
/// without reflection. A receptor without it is state only for repaired events.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs</code-under-test>
/// <docs>resilience/stream-integrity#repaired-events-and-receptors</docs>
public class ReceptorDiscoveryGeneratorRunOnRepairTests {
  private const string REGISTRY_FILE = "ReceptorRegistry.g.cs";

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_WithRunOnRepair_SetsRunOnRepair_OnEveryReceptorShapeAsync() {
    const string source = """
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;
      using Whizbang.Core.Messaging;

      namespace MyApp.Receptors;

      public sealed class OrderPlaced : IEvent { }
      public sealed class OrderShipped : IEvent { }
      public sealed class OrderPacked : IEvent { }
      public sealed class OrderBilled : IEvent { }
      public sealed class InvoiceRaised : IEvent { }
      public sealed class Noted : IEvent { }

      [RunOnRepair]
      public class AsyncVoidConverger : IReceptor<OrderPlaced> {
        public ValueTask HandleAsync(OrderPlaced message, CancellationToken ct = default) => ValueTask.CompletedTask;
      }

      [RunOnRepair]
      public class AsyncResponseConverger : IReceptor<OrderShipped, Noted> {
        public ValueTask<Noted> HandleAsync(OrderShipped message, CancellationToken ct = default) => ValueTask.FromResult(new Noted());
      }

      [RunOnRepair]
      public class SyncVoidConverger : ISyncReceptor<OrderPacked> {
        public void Handle(OrderPacked message) { }
      }

      [RunOnRepair]
      public class SyncResponseConverger : ISyncReceptor<OrderBilled, Noted> {
        public Noted Handle(OrderBilled message) => new();
      }

      public class InvoiceEmailer : IReceptor<InvoiceRaised> {
        public ValueTask HandleAsync(InvoiceRaised message, CancellationToken ct = default) => ValueTask.CompletedTask;
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, REGISTRY_FILE);
    await Assert.That(registry).IsNotNull();
    await Assert.That(_runOnRepairFlagsFor(registry!, "AsyncVoidConverger")).IsEquivalentTo(["true"]);
    await Assert.That(_runOnRepairFlagsFor(registry!, "AsyncResponseConverger")).IsEquivalentTo(["true"]);
    await Assert.That(_runOnRepairFlagsFor(registry!, "SyncVoidConverger")).IsEquivalentTo(["true"]);
    await Assert.That(_runOnRepairFlagsFor(registry!, "SyncResponseConverger")).IsEquivalentTo(["true"]);
    await Assert.That(_runOnRepairFlagsFor(registry!, "InvoiceEmailer")).IsEquivalentTo(["false"])
      .Because("the default is state only on repair: a receptor that did not opt in never runs for a repaired event");
    await Assert.That(registry).DoesNotContain("__RUN_ON_REPAIR__");
  }

  /// <summary>The distinct RunOnRepair values on every registry entry for one receptor class.</summary>
  private static string[] _runOnRepairFlagsFor(string registry, string receptorClass) {
    const string FLAG = "RunOnRepair: ";
    var marker = $"MyApp.Receptors.{receptorClass}\"";
    var flags = new HashSet<string>(StringComparer.Ordinal);
    foreach (var entry in registry.Split("ReceptorInfo(")) {
      var flagAt = entry.IndexOf(FLAG, StringComparison.Ordinal);
      if (flagAt < 0 || !entry.Contains(marker, StringComparison.Ordinal)) {
        continue;
      }
      var valueAt = flagAt + FLAG.Length;
      flags.Add(entry[valueAt..entry.IndexOfAny(['\r', '\n', ')', ','], valueAt)].Trim());
    }
    return [.. flags];
  }
}
