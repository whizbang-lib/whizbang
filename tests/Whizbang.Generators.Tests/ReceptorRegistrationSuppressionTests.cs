// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Locks the behavior of <c>[SuppressReceptorRegistration]</c>: a receptor that declares its owner
/// constructs it by hand must be discovered and routed, but never registered in DI.
/// </summary>
/// <remarks>
/// <para>
/// Discovery registering every receptor is the right default, but some are deliberately built by
/// their owner — a helper closing over caller state, or one parameterized by a callback. Their
/// constructors take arguments the container has no way to supply.
/// </para>
/// <para>
/// Registering one anyway is not a local problem. The container validates every registered
/// descriptor when the provider is built with validation enabled, so a SINGLE un-constructible
/// receptor aborts construction of the entire provider — every service in the assembly goes down,
/// and the resulting error names the receptor without ever explaining why it was registered.
/// </para>
/// <para>
/// Note what is deliberately NOT done: the framework does not skip receptors it merely fails to
/// construct. Doing that would turn a forgotten dependency registration into a receptor that
/// silently never fires. Opting out has to be written down, not inferred.
/// </para>
/// </remarks>
/// <docs>fundamentals/receptors/receptors#manual-construction</docs>
public class ReceptorRegistrationSuppressionTests {

  private const string HAND_BUILT_SOURCE = @"
using System;
using Whizbang.Core;

namespace MyApp.Receptors;

public record OrderPlaced : IEvent;

// Constructed by hand so it can close over state the container knows nothing about.
[SuppressReceptorRegistration]
public sealed class HandBuiltReceptor : IReceptor<OrderPlaced> {
  private readonly Action<OrderPlaced> _onReceived;
  public HandBuiltReceptor(Action<OrderPlaced> onReceived) { _onReceived = onReceived; }
  public System.Threading.Tasks.ValueTask HandleAsync(OrderPlaced message, System.Threading.CancellationToken cancellationToken = default) {
    _onReceived(message);
    return default;
  }
}
";

  private const string ORDINARY_SOURCE = @"
using Whizbang.Core;

namespace MyApp.Receptors;

public record OrderPlaced : IEvent;

public sealed class OrdinaryReceptor : IReceptor<OrderPlaced> {
  public System.Threading.Tasks.ValueTask HandleAsync(OrderPlaced message, System.Threading.CancellationToken cancellationToken = default) => default;
}
";

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SuppressedReceptor_IsNotRegisteredInDependencyInjectionAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(HAND_BUILT_SOURCE);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var registrations = GeneratorTestHelper.GetGeneratedSource(result, "DispatcherRegistrations.g.cs");
    await Assert.That(registrations).IsNotNull();

    await Assert.That(registrations).DoesNotContain("HandBuiltReceptor")
      .Because("its constructor takes an Action<T> the container cannot supply, so registering it "
             + "would leave an un-constructible descriptor — and under container validation one of "
             + "those aborts the ENTIRE service provider, not just this receptor");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task OrdinaryReceptor_IsStillRegisteredAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(ORDINARY_SOURCE);

    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);

    var registrations = GeneratorTestHelper.GetGeneratedSource(result, "DispatcherRegistrations.g.cs");
    await Assert.That(registrations).IsNotNull();

    await Assert.That(registrations).Contains("OrdinaryReceptor")
      .Because("suppression must be opt-IN — if the attribute's absence stopped registering "
             + "receptors, the guard would have silently disabled the default path it exists to "
             + "carve an exception out of");
  }

  /// <summary>Same hand-built shape, but the author never declared it — the case WHIZ014 exists for.</summary>
  private const string UNDECLARED_SOURCE = @"
using System;
using Whizbang.Core;

namespace MyApp.Receptors;

public record OrderPlaced : IEvent;

public sealed class UndeclaredReceptor : IReceptor<OrderPlaced> {
  private readonly Action<OrderPlaced> _onReceived;
  public UndeclaredReceptor(Action<OrderPlaced> onReceived) { _onReceived = onReceived; }
  public System.Threading.Tasks.ValueTask HandleAsync(OrderPlaced message, System.Threading.CancellationToken cancellationToken = default) {
    _onReceived(message);
    return default;
  }
}
";

  [Test]
  [RequiresAssemblyFiles()]
  public async Task UndeclaredHandBuiltReceptor_IsWarnedAboutAtTheParameterDiCannotSupplyAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(UNDECLARED_SOURCE);

    // #1429: reported where the cause is written, so it can be suppressed there
    var whiz014 = result.Diagnostics.Single(d => d.Id == "WHIZ014");
    await Assert.That(whiz014.Location.IsInSource).IsTrue();
    var text = await whiz014.Location.SourceTree!.GetTextAsync();
    await Assert.That(text.ToString(whiz014.Location.SourceSpan)).IsEqualTo("onReceived");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task UndeclaredHandBuiltReceptor_IsWarnedAboutAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(UNDECLARED_SOURCE);

    await Assert.That(result.Diagnostics.Any(d => d.Id == "WHIZ014")).IsTrue()
      .Because("this receptor WILL be registered and cannot be constructed, so it takes the entire "
             + "service provider down at startup. Warning at the declaration site trades an "
             + "inexplicable runtime crash for a message pointing at the cause");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task DeclaredHandBuiltReceptor_IsNotWarnedAboutAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(HAND_BUILT_SOURCE);

    await Assert.That(result.Diagnostics.Any(d => d.Id == "WHIZ014")).IsFalse()
      .Because("the attribute already declares this construction is deliberate and the receptor is "
             + "not registered, so its constructor is nobody's problem — warning anyway would be "
             + "noise that trains people to ignore the diagnostic");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task OrdinaryReceptor_IsNotWarnedAboutAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(ORDINARY_SOURCE);

    await Assert.That(result.Diagnostics.Any(d => d.Id == "WHIZ014")).IsFalse()
      .Because("a receptor with an injectable constructor must stay silent, or the heuristic is "
             + "firing on ordinary code and is worse than nothing");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SuppressedReceptor_IsStillDiscoveredAndRoutedAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(HAND_BUILT_SOURCE);

    var dispatcher = GeneratorTestHelper.GetGeneratedSource(result, "Dispatcher.g.cs");
    await Assert.That(dispatcher).IsNotNull();

    await Assert.That(dispatcher).Contains("OrderPlaced")
      .Because("the attribute declares who owns CONSTRUCTION, not that the type should be ignored. "
             + "Routing must still be generated for its message, or opting out of DI would quietly "
             + "cost the receptor its dispatch as well");
  }

  // ========================================
  // #2309: the compiled route of a suppressed receptor skips when nothing is registered
  // ========================================

  private const string SUPPRESSED_ROUTE_SOURCES = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tracing;

namespace MyApp.Receptors;

public record OrderPlaced : IMessage;
public record OrderQuoted : IMessage;
public record Quote(decimal Amount);

[FireAt(LifecycleStage.PostDistributeInline)]
[SuppressReceptorRegistration]
public sealed class HandBuiltVoidReceptor(Action<OrderPlaced> onReceived) : IReceptor<OrderPlaced> {
  public ValueTask HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default) { onReceived(message); return default; }
}

[FireAt(LifecycleStage.PostDistributeInline)]
[SuppressReceptorRegistration]
public sealed class HandBuiltResponseReceptor(Func<OrderQuoted, Quote> quote) : IReceptor<OrderQuoted, Quote> {
  public ValueTask<Quote> HandleAsync(OrderQuoted message, CancellationToken cancellationToken = default) => new(quote(message));
}

[FireAt(LifecycleStage.PostDistributeInline)]
[SuppressReceptorRegistration]
[WhizbangTrace]
public sealed class HandBuiltTracedReceptor(Action<OrderPlaced> onReceived) : IReceptor<OrderPlaced> {
  public ValueTask HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default) { onReceived(message); return default; }
}

[FireAt(LifecycleStage.PostDistributeInline)]
[SuppressReceptorRegistration]
[WhizbangTrace]
public sealed class HandBuiltTracedResponseReceptor(Func<OrderQuoted, Quote> quote) : IReceptor<OrderQuoted, Quote> {
  public ValueTask<Quote> HandleAsync(OrderQuoted message, CancellationToken cancellationToken = default) => new(quote(message));
}

[FireAt(LifecycleStage.PostDistributeInline)]
[SuppressReceptorRegistration]
public sealed class HandBuiltSyncReceptor(Action<OrderPlaced> onReceived) : ISyncReceptor<OrderPlaced> {
  public void Handle(OrderPlaced message) => onReceived(message);
}
";

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SuppressedReceptor_CompiledRoute_ResolvesThroughTheSuppressedRouteAndSkipsWhenUnregisteredAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(SUPPRESSED_ROUTE_SOURCES);

    var registry = GeneratorTestHelper.GetRequiredGeneratedSource(result, "ReceptorRegistry.g.cs");

    await Assert.That(registry).Contains(
        "global::Whizbang.Core.Messaging.SuppressedReceptorRoute.Resolve<global::Whizbang.Core.IReceptor<global::MyApp.Receptors.OrderPlaced>, global::MyApp.Receptors.HandBuiltVoidReceptor>(sp, \"global::MyApp.Receptors.HandBuiltVoidReceptor\")")
      .Because("the owner registers a suppressed receptor by hand or only at runtime, so its route must not demand one from DI");
    await Assert.That(registry).Contains(
        "global::Whizbang.Core.Messaging.SuppressedReceptorRoute.Resolve<global::Whizbang.Core.IReceptor<global::MyApp.Receptors.OrderQuoted, global::MyApp.Receptors.Quote>, global::MyApp.Receptors.HandBuiltResponseReceptor>")
      .Because("a receptor with a response resolves through the same route under its own interface");
    await Assert.That(registry).Contains(
        "global::Whizbang.Core.Messaging.SuppressedReceptorRoute.Resolve<global::Whizbang.Core.ISyncReceptor<global::MyApp.Receptors.OrderPlaced>, global::MyApp.Receptors.HandBuiltSyncReceptor>")
      .Because("a synchronous receptor resolves through the same route under its synchronous interface");
    await Assert.That(registry).DoesNotContain("GetRequiredService")
      .Because("every receptor in this source is suppressed, so no route may throw for a registration nobody was asked to make");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SuppressedTracedReceptor_ASkip_IsTracedAsAnEarlyReturnAsync() {
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(SUPPRESSED_ROUTE_SOURCES);

    var registry = GeneratorTestHelper.GetRequiredGeneratedSource(result, "ReceptorRegistry.g.cs");

    await Assert.That(registry).Contains("status = global::Whizbang.Core.Tracing.HandlerStatus.EarlyReturn;")
      .Because("a traced receptor that was skipped did not succeed: the trace says it returned early");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SuppressedReceptorRoutes_CompileAsync() {
    // The registry file carries its own usings; the generator's other files lean on a consumer's implicit usings,
    // which this harness does not supply, so only the file the routes live in is judged.
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<ReceptorDiscoveryGenerator>(SUPPRESSED_ROUTE_SOURCES)
      .Where(e => e.Location.SourceTree?.FilePath.EndsWith("ReceptorRegistry.g.cs", StringComparison.Ordinal) == true)
      .Select(e => e.ToString())
      .ToList();

    await Assert.That(errors).IsEmpty()
      .Because("the void, response, traced and synchronous suppressed routes must all compile");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task OrdinaryReceptor_CompiledRoute_StillRequiresItsRegistrationAsync() {
    // Locks the other half of the decision: a receptor without the attribute is registered by discovery, so a route
    // that cannot resolve it means a registration is genuinely missing, and that must still fail loudly.
    var result = GeneratorTestHelper.RunGenerator<ReceptorDiscoveryGenerator>(ORDINARY_SOURCE);

    var registry = GeneratorTestHelper.GetRequiredGeneratedSource(result, "ReceptorRegistry.g.cs");

    await Assert.That(registry).Contains("GetRequiredService<global::Whizbang.Core.IReceptor<global::MyApp.Receptors.OrderPlaced>>()");
    await Assert.That(registry).DoesNotContain("SuppressedReceptorRoute");
  }
}
