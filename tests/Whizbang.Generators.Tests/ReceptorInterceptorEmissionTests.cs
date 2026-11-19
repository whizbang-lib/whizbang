// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// #1996: every receptor invocation the generator emits asks the receptor interceptors first, naming the receptor
/// class, and every lifecycle registry entry names its receptor class so the receptor invoker can ask about it. The
/// Core tests compile and run this code end to end (ReceptorInterceptorDispatchTests); these pin its shape, so a
/// generator change that drops the gate from a path fails here by name.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/ReceptorDiscoveryGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/Templates/Snippets/DispatcherSnippets.cs</code-under-test>
[Category("SourceGenerators")]
[Category("ReceptorDiscovery")]
public class ReceptorInterceptorEmissionTests {
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

    public class AuditOrderPlaced : IReceptor<OrderPlaced> {
      public ValueTask HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    public class NotifyOrderPlaced : IReceptor<OrderPlaced> {
      public ValueTask HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
    """;

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SendInvokers_AskTheInterceptorsBeforeTheReceptorAsync() {
    var dispatcher = await _generatedAsync("Dispatcher.g.cs");

    await Assert.That(dispatcher).Contains(
      "if (!await AdmitReceptorAsync(typeof(global::MyApp.PlaceOrderReceptor), (global::MyApp.PlaceOrder)msg, scope.ServiceProvider, null, default)) {")
      .Because("the result and any-receptor invokers gate the receptor before resolving it");
    await Assert.That(dispatcher).Contains(
      "if (!await AdmitReceptorAsync(typeof(global::MyApp.LogNoteReceptor), (global::MyApp.LogNote)msg, scope.ServiceProvider, null, default)) {")
      .Because("the void invokers gate the receptor too");
    await Assert.That(dispatcher).Contains(
      "if (!AdmitReceptor(typeof(global::MyApp.PriceReceptor), (global::MyApp.Price)msg, scope.ServiceProvider)) {")
      .Because("a synchronous receptor is gated synchronously, on the caller's thread");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Publishers_AskTheInterceptorsBeforeEachReceptorAsync() {
    var dispatcher = await _generatedAsync("Dispatcher.g.cs");

    await Assert.That(dispatcher).Contains(
      "if (await AdmitReceptorAsync(typeof(global::MyApp.AuditOrderPlaced), typedEvt, scope.ServiceProvider, null, default)) {")
      .Because("the typed publisher gates each receptor of a published event");
    await Assert.That(dispatcher).Contains(
      "if (await AdmitReceptorAsync(typeof(global::MyApp.AuditOrderPlaced), typedEvt, scope.ServiceProvider, sourceEnvelope, cancellationToken)) {")
      .Because("the untyped publisher, which runs the receptors of a cascaded event, gates each one with the source envelope");
    await Assert.That(dispatcher).Contains(
      "if (await AdmitReceptorAsync(typeof(global::MyApp.NotifyOrderPlaced), typedMsg, scope.ServiceProvider, sourceEnvelope, cancellationToken)) {")
      .Because("the local runner for a message with two receptors gates each one");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task RegistryEntries_NameTheirReceptorClassAsync() {
    var registry = await _generatedAsync("ReceptorRegistry.g.cs");

    await Assert.That(registry).Contains("ReceptorType: typeof(global::MyApp.PlaceOrderReceptor)")
      .Because("the receptor invoker asks the interceptors about the receptor class before each lifecycle-stage receptor");
    await Assert.That(registry).Contains("ReceptorType: typeof(global::MyApp.PriceReceptor)")
      .Because("a synchronous receptor's entry is rewritten, and keeps its class");
  }

  private static async Task<string> _generatedAsync(string fileName) {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(SOURCE);
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, fileName);
    await Assert.That(generated).IsNotNull();
    return generated!;
  }
}
