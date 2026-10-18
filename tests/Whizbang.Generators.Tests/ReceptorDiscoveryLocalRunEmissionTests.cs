// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// What the generator emits for the local run (#1410 follow-up): one publisher block per message type
/// that has a receptor firing in-process, every handler shape invoked in it, a returned result cascaded
/// from both publishers, an interface receptor reached from each concrete message, and typed invokers
/// that report a result of another type instead of casting it. The component tests run this code; these
/// pin its shape, so a generator change that stops emitting a piece fails here by name.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/Templates/Snippets/DispatcherSnippets.cs</code-under-test>
public class ReceptorDiscoveryLocalRunEmissionTests {
  [Test]
  [RequiresAssemblyFiles()]
  public async Task LocalRun_ResultReceptor_CascadesItsResultFromBothPublishersAsync() {
    const string source = """
      using System;
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;

      namespace MyApp;

      public record PlaceOrder(Guid Id) : ICommand;
      public record OrderPlaced([property: StreamId] Guid Id) : IEvent;

      public class PlaceOrderReceptor : IReceptor<PlaceOrder, OrderPlaced> {
        public ValueTask<OrderPlaced> HandleAsync(PlaceOrder message, CancellationToken cancellationToken = default) =>
          ValueTask.FromResult(new OrderPlaced(message.Id));
      }
      """;

    var dispatcher = await _generatedDispatcherAsync(source);

    await Assert.That(dispatcher).Contains("var result = await r.HandleAsync(typedEvt);")
      .Because("the typed publisher runs the receptor and keeps its result");
    await Assert.That(dispatcher).Contains("await CascadeLocalResultAsync(result, typedEvt, typeof(global::MyApp.PlaceOrder), null, default);")
      .Because("the typed publisher cascades the result: #1410 dropped it");
    await Assert.That(dispatcher).Contains("await CascadeLocalResultAsync(result, typedEvt, typeof(global::MyApp.PlaceOrder), sourceEnvelope, cancellationToken);")
      .Because("the untyped publisher cascades it with the source envelope and the caller's token");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task LocalRun_SyncReceptors_AreInvokedInThePublisherAndTheirResultCascadesAsync() {
    const string source = """
      using System;
      using Whizbang.Core;

      namespace MyApp;

      public record Pinged([property: StreamId] Guid Id) : IEvent;
      public record Ponged([property: StreamId] Guid Id) : IEvent;
      public record Noted([property: StreamId] Guid Id) : IEvent;

      public class PingedReceptor : ISyncReceptor<Pinged, Ponged> {
        public Ponged Handle(Pinged message) => new(message.Id);
      }

      public class NotedReceptor : ISyncReceptor<Noted> {
        public void Handle(Noted message) { }
      }
      """;

    var dispatcher = await _generatedDispatcherAsync(source);

    await Assert.That(dispatcher).Contains("GetKeyedService<global::Whizbang.Core.ISyncReceptor<global::MyApp.Pinged, global::MyApp.Ponged>>(\"global::MyApp.PingedReceptor\")")
      .Because("a sync receptor that returns a result runs in the local run");
    await Assert.That(dispatcher).Contains("var result = r.Handle(typedEvt);");
    await Assert.That(dispatcher).Contains("await CascadeLocalResultAsync(result, typedEvt, typeof(global::MyApp.Pinged), null, default);")
      .Because("a sync receptor's result cascades as an async receptor's does");
    await Assert.That(dispatcher).Contains("GetKeyedService<global::Whizbang.Core.ISyncReceptor<global::MyApp.Noted>>(\"global::MyApp.NotedReceptor\")")
      .Because("a void sync receptor runs in the local run");
    await Assert.That(dispatcher).Contains("r.Handle(typedEvt);");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task LocalRun_InterfaceReceptor_RunsForEveryConcreteMessageThatImplementsItAsync() {
    const string source = """
      using System;
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;

      namespace MyApp;

      public interface IShapeChanged : IEvent;
      public record CircleChanged([property: StreamId] Guid Id) : IShapeChanged;
      public record SquareChanged([property: StreamId] Guid Id) : IShapeChanged;

      public class ShapeChangedReceptor : IReceptor<IShapeChanged> {
        public ValueTask HandleAsync(IShapeChanged message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
      }
      """;

    var dispatcher = await _generatedDispatcherAsync(source);

    foreach (var concrete in new[] { "CircleChanged", "SquareChanged" }) {
      var block = _publisherBlockFor(dispatcher, concrete);
      await Assert.That(block).Contains("GetKeyedService<global::Whizbang.Core.IReceptor<global::MyApp.IShapeChanged>>(\"global::MyApp.ShapeChangedReceptor\")")
        .Because($"a receptor of an interface runs when a {concrete} is handled here");
    }
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task LocalRun_ReceptorOnlyAtAPipelineStage_GetsNoPublisherBlockAsync() {
    const string source = """
      using System;
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;
      using Whizbang.Core.Messaging;

      namespace MyApp;

      public record Arrived([property: StreamId] Guid Id) : IEvent;

      [FireAt(LifecycleStage.PostInboxInline)]
      public class ArrivedObserver : IReceptor<Arrived> {
        public ValueTask HandleAsync(Arrived message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
      }
      """;

    var dispatcher = await _generatedDispatcherAsync(source);

    await Assert.That(dispatcher).DoesNotContain("eventType == typeof(global::MyApp.Arrived)")
      .Because("a receptor declared only at a pipeline stage fires there through the receptor invoker, never in the local run");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TypedInvoker_ResultOfAnotherType_ReportsTheMismatchInsteadOfCastingAsync() {
    const string source = """
      using System;
      using System.Threading;
      using System.Threading.Tasks;
      using Whizbang.Core;

      namespace MyApp;

      public record GetPrice(Guid Id) : ICommand;
      public record Price(decimal Amount);

      public class GetPriceReceptor : IReceptor<GetPrice, Price> {
        public ValueTask<Price> HandleAsync(GetPrice message, CancellationToken cancellationToken = default) => ValueTask.FromResult(new Price(1m));
      }
      """;

    var dispatcher = await _generatedDispatcherAsync(source);

    await Assert.That(dispatcher).Contains(": throw new global::Whizbang.Core.Dispatch.ReceptorResultMismatchException(result, typeof(TResult));")
      .Because("a result the caller did not ask for is reported with the result, so the dispatcher can use it without running the receptor again");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Registry_SyncReceptor_InvokesHandleAndReturnsTheResultAsItIsAsync() {
    const string source = """
      using System;
      using Whizbang.Core;

      namespace MyApp;

      public record Pinged([property: StreamId] Guid Id) : IEvent;
      public record Ponged([property: StreamId] Guid Id) : IEvent;

      public class PingedReceptor : ISyncReceptor<Pinged, Ponged> {
        public Ponged Handle(Pinged message) => new(message.Id);
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var registry = GeneratorTestHelper.GetGeneratedSource(result, "ReceptorRegistry.g.cs");

    await Assert.That(registry).IsNotNull();
    await Assert.That(registry!).Contains("ReceptorId: \"global::MyApp.PingedReceptor\"")
      .Because("a sync receptor has registry entries, so the worker stages reach it");
    await Assert.That(registry).Contains("return await global::System.Threading.Tasks.ValueTask.FromResult(receptor.Handle((global::MyApp.Pinged)msg));")
      .Because("the entry invokes Handle and returns the result as it is, so a Routed<T> result keeps its route");
  }

  private static async Task<string> _generatedDispatcherAsync(string source) {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(source);
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();
    return dispatcher!;
  }

  /// <summary>The untyped publisher block guarded by the message type, up to the next block.</summary>
  private static string _publisherBlockFor(string dispatcher, string typeName) {
    var guard = $"if (eventType == typeof(global::MyApp.{typeName})) {{";
    var start = dispatcher.IndexOf(guard, StringComparison.Ordinal);
    if (start < 0) {
      return string.Empty;
    }
    var next = dispatcher.IndexOf("if (eventType == typeof(", start + guard.Length, StringComparison.Ordinal);
    return next < 0 ? dispatcher[start..] : dispatcher[start..next];
  }
}
