// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// #2633: every receptor body the generated local dispatch runs (send, void, sync, void sync, any, the local runner and
/// both publishers) runs inside <c>EnterLocalReceptorHandling</c>, naming the receptor by its registry id, so a receptor
/// reached locally from inside a handling derives in a handling of its own. The Core tests run this code end to end
/// (LocalInvokeDeriveFromTests); these pin its shape, so a generator change that drops it from a path fails here by name.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/Templates/Snippets/DispatcherSnippets.cs</code-under-test>
[Category("SourceGenerators")]
[Category("ReceptorDiscovery")]
public partial class LocalReceptorHandlingEmissionTests {
  private const string SOURCE = """
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Whizbang.Core;

    namespace MyApp;

    public record PlaceOrder(Guid Id) : ICommand;
    public record OrderPlaced([property: StreamId] Guid Id) : IEvent;
    public record LogNote(Guid Id) : ICommand;
    public record Price(Guid Id) : ICommand;
    public record Priced(Guid Id);
    public record Ping(Guid Id) : ICommand;

    public class PlaceOrderReceptor : IReceptor<PlaceOrder, OrderPlaced> {
      public ValueTask<OrderPlaced> HandleAsync(PlaceOrder message, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new OrderPlaced(message.Id));
    }

    public class LogNoteReceptor : IReceptor<LogNote> {
      public ValueTask HandleAsync(LogNote message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    public class PriceReceptor : ISyncReceptor<Price, Priced> {
      public Priced Handle(Price message) => new(message.Id);
    }

    public class PingReceptor : ISyncReceptor<Ping> {
      public void Handle(Ping message) { }
    }

    public class AuditOrderPlaced : IReceptor<OrderPlaced> {
      public ValueTask HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    public class NotifyOrderPlaced : IReceptor<OrderPlaced, Priced> {
      public ValueTask<Priced> HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new Priced(message.Id));
    }
    """;

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EveryReceptorBodyTheDispatcherRuns_RunsInsideItsLocalHandlingAsync() {
    var dispatcher = await _generatedAsync();
    var lines = dispatcher.Split('\n');

    // Every call of a receptor's body in the generated dispatcher, on every path, sits directly inside the handling
    // entered for that receptor: a path added without it fails here
    var bodies = 0;
    for (var i = 0; i < lines.Length; i++) {
      if (!_bodyCall().IsMatch(lines[i])) {
        continue;
      }
      bodies++;
      await Assert.That(lines[i - 1]).Contains("using (EnterLocalReceptorHandling(\"global::MyApp.")
        .Because($"the body call '{lines[i].Trim()}' runs in the receptor's local handling (#2633)");
    }
    await Assert.That(bodies).IsGreaterThanOrEqualTo(12)
      .Because("send, void, any, sync and void sync invokers, both publishers and the local runner each run a body");
  }

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("PlaceOrderReceptor")]
  [Arguments("LogNoteReceptor")]
  [Arguments("PriceReceptor")]
  [Arguments("PingReceptor")]
  [Arguments("AuditOrderPlaced")]
  [Arguments("NotifyOrderPlaced")]
  public async Task EachKindOfReceptor_NamesItselfInItsLocalHandlingAsync(string receptor) {
    var dispatcher = await _generatedAsync();

    await Assert.That(dispatcher).Contains($"using (EnterLocalReceptorHandling(\"global::MyApp.{receptor}\")) {{")
      .Because("the handling names the receptor by its registry id, as the receptor invoker's does");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AResult_CascadesAfterTheBodysHandlingEndsAsync() {
    var dispatcher = await _generatedAsync();

    await Assert.That(_resultCascadesAfterTheHandling().IsMatch(dispatcher)).IsTrue()
      .Because("a result cascades after the body's handling ends, as the receptor invoker cascades one");
  }

  [System.Text.RegularExpressions.GeneratedRegex(@"result = await r\.HandleAsync\(typedEvt[^;]*\);\s*\}\s*await CascadeLocalResultAsync\(result, typedEvt")]
  private static partial System.Text.RegularExpressions.Regex _resultCascadesAfterTheHandling();

  [System.Text.RegularExpressions.GeneratedRegex(@"\b(receptor|r)\.(HandleAsync|Handle)\(typed(Msg|Evt)")]
  private static partial System.Text.RegularExpressions.Regex _bodyCall();

  private static async Task<string> _generatedAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(SOURCE);
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(generated).IsNotNull();
    return generated!;
  }
}
