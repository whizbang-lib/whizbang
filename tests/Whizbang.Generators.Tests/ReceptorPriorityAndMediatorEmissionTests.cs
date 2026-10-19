// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// What the receptor discovery generator emits for receptor priority and the local runner (#1412), and for
/// the interfaces and base types local receptors are declared for (#1418).
/// </summary>
/// <code-under-test>src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs</code-under-test>
/// <docs>fundamentals/receptors/receptor-priority</docs>
public class ReceptorPriorityAndMediatorEmissionTests {
  private const string MULTI_RECEPTOR_SOURCE = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Receptors;

public record Ship(System.Guid Id) : ICommand;
public record Shipped(System.Guid Id);
public record Lonely(System.Guid Id) : ICommand;

[ReceptorPriority(ReceptorPriority.LATE)]
public class ShipLate : IReceptor<Ship> {
  public ValueTask HandleAsync(Ship message, CancellationToken ct = default) => ValueTask.CompletedTask;
}

public class ShipAnswers : IReceptor<Ship, Shipped> {
  public ValueTask<Shipped> HandleAsync(Ship message, CancellationToken ct = default) => ValueTask.FromResult(new Shipped(message.Id));
}

[ReceptorPriority(ReceptorPriority.EARLY)]
public class ShipEarly : IReceptor<Ship> {
  public ValueTask HandleAsync(Ship message, CancellationToken ct = default) => ValueTask.CompletedTask;
}

public class LonelyReceptor : IReceptor<Lonely> {
  public ValueTask HandleAsync(Lonely message, CancellationToken ct = default) => ValueTask.CompletedTask;
}
";

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_IsEmittedForAMessageWithSeveralReceptors_InPriorityOrderAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(MULTI_RECEPTOR_SOURCE);
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs")!;

    var runner = _section(dispatcher, "GetLocalReceptorRunner<TAnswer>(Type messageType, bool wantsAnswer)");
    await Assert.That(runner).Contains("typeof(global::MyApp.Receptors.Ship)");
    var early = runner.IndexOf("\"global::MyApp.Receptors.ShipEarly\"", StringComparison.Ordinal);
    var answers = runner.IndexOf("\"global::MyApp.Receptors.ShipAnswers\"", StringComparison.Ordinal);
    var late = runner.IndexOf("\"global::MyApp.Receptors.ShipLate\"", StringComparison.Ordinal);
    await Assert.That(early).IsGreaterThan(-1);
    await Assert.That(answers).IsGreaterThan(early).Because("an undeclared receptor runs at the middle default, after EARLY");
    await Assert.That(late).IsGreaterThan(answers).Because("LATE runs after the default");
    await Assert.That(runner).Contains("TryTakeLocalAnswer<TAnswer, ");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_IsNotEmittedForAMessageWithOneReceptorAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(MULTI_RECEPTOR_SOURCE);
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs")!;

    var runner = _section(dispatcher, "GetLocalReceptorRunner<TAnswer>(Type messageType, bool wantsAnswer)");
    await Assert.That(runner).DoesNotContain("typeof(global::MyApp.Receptors.Lonely)")
      .Because("a message with one receptor keeps the single-receptor lookups, and their zero-allocation path");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SendLookups_PickTheEarliestReceptorByPriorityAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(MULTI_RECEPTOR_SOURCE);
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs")!;

    var voidSend = _section(dispatcher, "GetVoidReceptorInvoker(object message, Type messageType)");
    await Assert.That(voidSend).Contains("\"global::MyApp.Receptors.ShipEarly\"");
    await Assert.That(voidSend).DoesNotContain("\"global::MyApp.Receptors.ShipLate\"")
      .Because("the single void lookup names the earliest void receptor by priority, not the first declared");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PolymorphicReceptorMessageTypes_ListTheInterfacesLocalReceptorsAreDeclaredForAsync() {
    const string source = @"
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace MyApp.Receptors;

public interface IAnswer : IMessage { }
public sealed record Concrete : ICommand;

public class AnswerReceptor : IReceptor<IAnswer> {
  public ValueTask HandleAsync(IAnswer message, CancellationToken ct = default) => ValueTask.CompletedTask;
}

public class ConcreteReceptor : IReceptor<Concrete> {
  public ValueTask HandleAsync(Concrete message, CancellationToken ct = default) => ValueTask.CompletedTask;
}

[FireAt(LifecycleStage.PostInboxInline)]
public class StageOnly : IReceptor<IStageAnswer> {
  public ValueTask HandleAsync(IStageAnswer message, CancellationToken ct = default) => ValueTask.CompletedTask;
}

public interface IStageAnswer : IMessage { }
";
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs")!;

    var table = _section(dispatcher, "_polymorphicReceptorMessageTypes = [");
    await Assert.That(table).Contains("typeof(global::MyApp.Receptors.IAnswer),");
    await Assert.That(table).DoesNotContain("Concrete").Because("a receptor of a sealed type is matched exactly, so it needs no entry");
    await Assert.That(table).DoesNotContain("IStageAnswer").Because("a receptor declared only at a pipeline stage does not handle local dispatch");
  }

  /// <summary>The generated text from <paramref name="start"/> to the end of that member's block.</summary>
  private static string _section(string source, string start) {
    var from = source.IndexOf(start, StringComparison.Ordinal);
    if (from < 0) {
      return string.Empty;
    }
    var next = source.IndexOf("\n  protected override", from + start.Length, StringComparison.Ordinal);
    var end = source.IndexOf("];", from, StringComparison.Ordinal);
    var stop = start.EndsWith('[') ? end : next;
    return stop < 0 ? source[from..] : source[from..stop];
  }
}
