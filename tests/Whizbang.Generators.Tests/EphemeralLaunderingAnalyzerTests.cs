// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// WHIZ132: ephemeral-to-Sourced laundering. A receptor whose input message is ephemeral and that returns or cascades a
/// Sourced (non-ephemeral) event turns data that was declared not to be kept into a durable fact in the event store.
/// The rule is the simple one: any statically known Sourced event in the receptor's response type, whether or not it
/// copies the input. <c>[DangerouslyAllowSourcedEventsFromEphemeralInput]</c> opts a deliberate receptor out.
/// </summary>
/// <tests>src/Whizbang.Generators/EphemeralAnalyzer.cs</tests>
[Category("Analyzers")]
public class EphemeralLaunderingAnalyzerTests {
  private const string HEADER = """
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Whizbang.Core;
    using Whizbang.Core.Attributes;
    using Whizbang.Core.Dispatch;
    namespace TestApp;
    [Ephemeral]
    public record PresencePing : IEvent;
    [Ephemeral]
    public record PresenceGone : IEvent;
    public record OrderPlaced : IEvent;
    public record OrderShipped : IEvent;

    """;

  // Every source must compile: a receptor the compiler could not bind would leave a negative test passing for the
  // wrong reason.
  private static async Task<Diagnostic[]> _whiz132Async(string body) {
    var errors = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(HEADER + body).GetDiagnostics()
      .Where(d => d.Severity == DiagnosticSeverity.Error)
      .Select(d => d.ToString())
      .ToArray();
    await Assert.That(errors).IsEmpty();

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>(HEADER + body);
    return [.. diagnostics.Where(d => d.Id == "WHIZ132")];
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task EphemeralInput_ReturningASourcedEvent_ReportsWHIZ132WarningAsync() {
    var found = await _whiz132Async("""
      public class PresenceToOrder : IReceptor<PresencePing, OrderPlaced> {
        public ValueTask<OrderPlaced> HandleAsync(PresencePing message, CancellationToken cancellationToken = default) =>
          new(new OrderPlaced());
      }
      """);

    await Assert.That(found.Length).IsEqualTo(1);
    await Assert.That(found[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    var message = found[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("PresenceToOrder");
    await Assert.That(message).Contains("PresencePing");
    await Assert.That(message).Contains("OrderPlaced");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task SyncReceptor_ReturningASourcedEvent_ReportsWHIZ132Async() {
    var found = await _whiz132Async("""
      public class PresenceToOrder : ISyncReceptor<PresencePing, OrderPlaced> {
        public OrderPlaced Handle(PresencePing message) => new();
      }
      """);

    await Assert.That(found.Length).IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task CascadedShapes_TupleArrayListAndRouted_ReportOnlyTheSourcedEventsAsync() {
    // The cascade unwraps tuples, arrays, collections, Routed<T> and nullable values; the ephemeral events in the same
    // response are not named.
    var found = await _whiz132Async("""
      public class TupleOut : IReceptor<PresencePing, (PresenceGone, OrderPlaced?)> {
        public ValueTask<(PresenceGone, OrderPlaced?)> HandleAsync(PresencePing message, CancellationToken cancellationToken = default) =>
          new((new PresenceGone(), null));
      }
      public class ArrayOut : ISyncReceptor<PresencePing, OrderPlaced[]> {
        public OrderPlaced[] Handle(PresencePing message) => [];
      }
      public class Listed : ISyncReceptor<PresencePing, IReadOnlyList<OrderShipped>> {
        public IReadOnlyList<OrderShipped> Handle(PresencePing message) => [];
      }
      public class Enumerated : ISyncReceptor<PresencePing, IEnumerable<OrderPlaced>> {
        public IEnumerable<OrderPlaced> Handle(PresencePing message) => [];
      }
      public class RoutedOne : ISyncReceptor<PresencePing, Routed<OrderPlaced>> {
        public Routed<OrderPlaced> Handle(PresencePing message) => Route.Local(new OrderPlaced());
      }
      public class Nested : ISyncReceptor<PresencePing, (RoutedNone, (PresenceGone, List<OrderShipped>))> {
        public (RoutedNone, (PresenceGone, List<OrderShipped>)) Handle(PresencePing message) => default;
      }
      """);

    await Assert.That(found.Length).IsEqualTo(6);
    var messages = found.Select(d => d.GetMessage(CultureInfo.InvariantCulture)).ToArray();
    await Assert.That(messages.Count(m => m.Contains("PresenceGone"))).IsEqualTo(0);
    await Assert.That(messages.Count(m => m.Contains("OrderShipped"))).IsEqualTo(2);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task EphemeralInputViaAProfileInterface_ReportsWHIZ132Async() {
    var found = await _whiz132Async("""
      [Ephemeral(Destruction = Destruction.WhenConsumed, Storage = TransientStorage.TtlRow)]
      public interface ISessionSignal : IEvent { }
      public record TabOpened : ISessionSignal;
      public class TabToOrder : ISyncReceptor<TabOpened, OrderPlaced> {
        public OrderPlaced Handle(TabOpened message) => new();
      }
      """);

    await Assert.That(found.Length).IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task OneDiagnosticPerEphemeralInput_OnAReceptorHandlingSeveralAsync() {
    var found = await _whiz132Async("""
      public class Presence : ISyncReceptor<PresencePing, OrderPlaced>, ISyncReceptor<PresenceGone, OrderShipped>,
          ISyncReceptor<OrderPlaced, OrderShipped> {
        public OrderPlaced Handle(PresencePing message) => new();
        public OrderShipped Handle(PresenceGone message) => new();
        public OrderShipped Handle(OrderPlaced message) => new();
      }
      """);

    await Assert.That(found.Length).IsEqualTo(2);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task ReceptorsThatKeepNothingDurable_NoWHIZ132Async() {
    // Ephemeral in, ephemeral out; Sourced in, Sourced out; nothing out; a response that is not an event; and a
    // response whose concrete event is unknown at compile time (an interface or an abstract base, which may be either
    // mode) are all left alone.
    var found = await _whiz132Async("""
      public abstract record OrderEvent : IEvent;
      public class EphemeralOut : ISyncReceptor<PresencePing, PresenceGone> {
        public PresenceGone Handle(PresencePing message) => new();
      }
      public class SourcedOut : ISyncReceptor<OrderPlaced, OrderShipped> {
        public OrderShipped Handle(OrderPlaced message) => new();
      }
      public class NothingOut : IReceptor<PresencePing> {
        public ValueTask HandleAsync(PresencePing message, CancellationToken cancellationToken = default) => default;
      }
      public class NotAnEvent : ISyncReceptor<PresencePing, string> {
        public string Handle(PresencePing message) => "";
      }
      public class Counted : ISyncReceptor<PresencePing, int?> {
        public int? Handle(PresencePing message) => null;
      }
      public class OpenSet : ISyncReceptor<PresencePing, (IEvent, OrderEvent, IEvent[])> {
        public (IEvent, OrderEvent, IEvent[]) Handle(PresencePing message) => default;
      }
      public class Open<TResponse> : ISyncReceptor<PresencePing, TResponse> {
        public TResponse Handle(PresencePing message) => default!;
      }
      public class NotAReceptor {
        public OrderPlaced Handle(PresencePing message) => new();
      }
      public interface IPresenceReceptor : ISyncReceptor<PresencePing, OrderPlaced> { }
      """);

    await Assert.That(found.Length).IsEqualTo(0);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task DeliberateReceptor_WithTheOptIn_NoWHIZ132Async() {
    var found = await _whiz132Async("""
      [DangerouslyAllowSourcedEventsFromEphemeralInput]
      public class PresenceToOrder : ISyncReceptor<PresencePing, OrderPlaced> {
        public OrderPlaced Handle(PresencePing message) => new();
      }
      """);

    await Assert.That(found.Length).IsEqualTo(0);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task UnboundOptIn_StillSuppressesWHIZ132AndAnUnrelatedAttributeDoesNotAsync() {
    // Matched by simple name, like WHIZ130's opt-in: written without its namespace in scope the attribute is an error
    // type carrying the name as written, and it still counts.
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<EphemeralAnalyzer>("""
      using Whizbang.Core;
      namespace TestApp;
      [Whizbang.Core.Attributes.Ephemeral]
      public record PresencePing : IEvent;
      public record OrderPlaced : IEvent;
      [DangerouslyAllowSourcedEventsFromEphemeralInput]
      public class Unbound : ISyncReceptor<PresencePing, OrderPlaced> {
        public OrderPlaced Handle(PresencePing message) => new();
      }
      [System.Obsolete]
      public class Unrelated : ISyncReceptor<PresencePing, OrderPlaced> {
        public OrderPlaced Handle(PresencePing message) => new();
      }
      """);

    var found = diagnostics.Where(d => d.Id == "WHIZ132").ToArray();
    await Assert.That(found.Length).IsEqualTo(1);
    await Assert.That(found[0].GetMessage(CultureInfo.InvariantCulture)).Contains("Unrelated");
  }
}
