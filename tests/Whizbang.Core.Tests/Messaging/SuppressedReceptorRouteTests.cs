// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A receptor declared <see cref="SuppressReceptorRegistrationAttribute"/> is owned by its author, who registers it by
/// hand in dependency injection or only at runtime through <see cref="IReceptorRegistry"/> (#2309). Its compiled route
/// runs the owner's instance when there is one and skips the receptor, logging the skip once at Debug, when there is
/// not. Before this the route used <c>GetRequiredService</c> and threw on every message of the type at its stages.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/SuppressedReceptorRoute.cs</code-under-test>
[Category("Messaging")]
public class SuppressedReceptorRouteTests {

  // ========================================
  // The generated route, end to end: this assembly's own generated registry routes SuppressedRouteProbeReceptor
  // ========================================

  [Test]
  public async Task GeneratedRoute_NothingRegistered_SkipsTheReceptorInsteadOfThrowingAsync() {
    var entry = _generatedEntry();
    await using var provider = new ServiceCollection().BuildServiceProvider();

    var message = new SuppressedRouteProbeEvent();
    var result = await entry.InvokeAsync(provider, message, _envelope(message), null, CancellationToken.None);

    await Assert.That(result).IsNull()
      .Because("a suppressed receptor nothing registered in DI is skipped: its owner may run it only through the runtime registry");
  }

  [Test]
  public async Task GeneratedRoute_RegisteredByHand_RunsTheOwnersInstanceAsync() {
    var received = new List<SuppressedRouteProbeEvent>();
    var entry = _generatedEntry();
    var services = new ServiceCollection();
    services.AddSingleton(new SuppressedRouteProbeReceptor(received.Add));
    await using var provider = services.BuildServiceProvider();
    var message = new SuppressedRouteProbeEvent();

    await entry.InvokeAsync(provider, message, _envelope(message), null, CancellationToken.None);

    await Assert.That(received).Contains(message)
      .Because("an instance the owner registered by hand is the one the compiled route runs");
  }

  [Test]
  public async Task GeneratedRoute_RegisteredAtRuntime_RunsTheRuntimeEntryAndTheCompiledRouteSkipsAsync() {
    var received = new List<SuppressedRouteProbeEvent>();
    var registry = new Whizbang.Core.Tests.Generated.GeneratedReceptorRegistry();
    var receptor = new SuppressedRouteProbeReceptor(received.Add);
    registry.Register<SuppressedRouteProbeEvent>(receptor, LifecycleStage.PostDistributeInline);
    await using var provider = new ServiceCollection().BuildServiceProvider();
    var invoker = new ReceptorInvoker(registry, provider);
    var message = new SuppressedRouteProbeEvent();

    await invoker.InvokeAsync(_envelope(message), LifecycleStage.PostDistributeInline);

    await Assert.That(received).Count().IsEqualTo(1)
      .Because("the runtime registration runs the owner's instance once, and the compiled route for the same class skips");
  }

  // ========================================
  // How the route resolves
  // ========================================

  [Test]
  public async Task Resolve_KeyedByClassName_ReturnsItAsync() {
    var mine = new KeyedReceptor();
    var services = new ServiceCollection();
    services.AddKeyedSingleton<IProbeReceptor>(nameof(KeyedReceptor), mine);
    await using var provider = services.BuildServiceProvider();

    var resolved = SuppressedReceptorRoute.Resolve<IProbeReceptor, KeyedReceptor>(provider, nameof(KeyedReceptor));

    await Assert.That(resolved).IsSameReferenceAs(mine);
  }

  [Test]
  public async Task Resolve_RegisteredAsItsClass_ReturnsItAsync() {
    var mine = new ClassRegisteredReceptor();
    await using var provider = new ServiceCollection().AddSingleton(mine).BuildServiceProvider();

    var resolved = SuppressedReceptorRoute.Resolve<IProbeReceptor, ClassRegisteredReceptor>(provider, nameof(ClassRegisteredReceptor));

    await Assert.That(resolved).IsSameReferenceAs(mine);
  }

  [Test]
  public async Task Resolve_RegisteredAsTheMessagesReceptorInterface_ReturnsItAsync() {
    var mine = new InterfaceRegisteredReceptor();
    await using var provider = new ServiceCollection().AddSingleton<IProbeReceptor>(mine).BuildServiceProvider();

    var resolved = SuppressedReceptorRoute.Resolve<IProbeReceptor, InterfaceRegisteredReceptor>(provider, nameof(InterfaceRegisteredReceptor));

    await Assert.That(resolved).IsSameReferenceAs(mine);
  }

  [Test]
  public async Task Resolve_ADifferentReceptorRegisteredForTheMessage_IsNotTakenForThisOneAsync() {
    // The container hands back the last receptor registered for the message, which can be another receptor entirely.
    // Running it under this receptor's route would run the wrong receptor, and run it twice.
    await using var provider = new ServiceCollection().AddSingleton<IProbeReceptor>(new ClassRegisteredReceptor()).BuildServiceProvider();

    var resolved = SuppressedReceptorRoute.Resolve<IProbeReceptor, UnregisteredReceptor>(provider, nameof(UnregisteredReceptor));

    await Assert.That(resolved).IsNull();
  }

  [Test]
  public async Task Resolve_NothingRegistered_LogsTheSkipOnceAtDebugAsync() {
    var logs = new FakeLogCollector();
    var services = new ServiceCollection();
    services.AddSingleton<ILoggerFactory>(new CollectorLoggerFactory(logs));
    await using var provider = services.BuildServiceProvider();

    var first = SuppressedReceptorRoute.Resolve<IProbeReceptor, LoggedOnceReceptor>(provider, nameof(LoggedOnceReceptor));
    var second = SuppressedReceptorRoute.Resolve<IProbeReceptor, LoggedOnceReceptor>(provider, nameof(LoggedOnceReceptor));

    await Assert.That(first).IsNull();
    await Assert.That(second).IsNull();
    var records = logs.GetSnapshot();
    await Assert.That(records.Count).IsEqualTo(1)
      .Because("the skip is logged once per receptor type, not once per message");
    await Assert.That(records[0].Level).IsEqualTo(LogLevel.Debug)
      .Because("a suppressed receptor its owner runs only at runtime is a supported setup, not an error");
    await Assert.That(records[0].Category).IsEqualTo(SuppressedReceptorRoute.LOGGER_CATEGORY);
    await Assert.That(records[0].Message).Contains(nameof(LoggedOnceReceptor));
    await Assert.That(records[0].Message).Contains("[SuppressReceptorRegistration]");
  }

  [Test]
  public async Task Resolve_NothingRegisteredAndNoLogger_ReportsTheSkipOnceALoggerExistsAsync() {
    await using var withoutLogger = new ServiceCollection().BuildServiceProvider();
    var logs = new FakeLogCollector();
    await using var withLogger = new ServiceCollection().AddSingleton<ILoggerFactory>(new CollectorLoggerFactory(logs)).BuildServiceProvider();

    var unlogged = SuppressedReceptorRoute.Resolve<IProbeReceptor, LateLoggerReceptor>(withoutLogger, nameof(LateLoggerReceptor));
    var logged = SuppressedReceptorRoute.Resolve<IProbeReceptor, LateLoggerReceptor>(withLogger, nameof(LateLoggerReceptor));

    await Assert.That(unlogged).IsNull();
    await Assert.That(logged).IsNull();
    await Assert.That(logs.GetSnapshot().Count).IsEqualTo(1)
      .Because("a host with no logger cannot report the skip, so the first host that can still does");
  }

  [Test]
  public async Task Resolve_NullServices_ThrowsAsync() {
    await Assert.That(() => SuppressedReceptorRoute.Resolve<IProbeReceptor, KeyedReceptor>(null!, nameof(KeyedReceptor)))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // Helpers
  // ========================================

  private static ReceptorInfo _generatedEntry() =>
    new Whizbang.Core.Tests.Generated.GeneratedReceptorRegistry()
      .GetReceptorsFor(typeof(SuppressedRouteProbeEvent), LifecycleStage.PostDistributeInline)
      .Single(r => r.ReceptorId.Contains(nameof(SuppressedRouteProbeReceptor), StringComparison.Ordinal));

  private static MessageEnvelope<SuppressedRouteProbeEvent> _envelope(SuppressedRouteProbeEvent message) =>
    new() {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

  /// <summary>
  /// Stands in for a receptor interface, so these classes exercise the route without being receptors this assembly's
  /// generator would discover and route.
  /// </summary>
  private interface IProbeReceptor;

  // One receptor class per test: the skip is reported once per receptor type for the whole process.
  private sealed class KeyedReceptor : IProbeReceptor;

  private sealed class ClassRegisteredReceptor : IProbeReceptor;

  private sealed class InterfaceRegisteredReceptor : IProbeReceptor;

  private sealed class UnregisteredReceptor : IProbeReceptor;

  private sealed class LoggedOnceReceptor : IProbeReceptor;

  private sealed class LateLoggerReceptor : IProbeReceptor;

  /// <summary>Hands out fake loggers under the category asked for, all writing to one collector.</summary>
  private sealed class CollectorLoggerFactory(FakeLogCollector logs) : ILoggerFactory {
    public ILogger CreateLogger(string categoryName) => new FakeLogger(logs, categoryName);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
  }
}

/// <summary>The message <see cref="SuppressedRouteProbeReceptor"/> handles; nothing else in this assembly does.</summary>
public sealed record SuppressedRouteProbeEvent : IMessage;

/// <summary>
/// Built by hand, so it closes over a callback the container cannot supply; this assembly's generator compiles a route
/// for it at PostDistributeInline that <see cref="SuppressedReceptorRouteTests"/> invokes.
/// </summary>
[FireAt(LifecycleStage.PostDistributeInline)]
[SuppressReceptorRegistration]
public sealed class SuppressedRouteProbeReceptor(Action<SuppressedRouteProbeEvent> onReceived) : IReceptor<SuppressedRouteProbeEvent> {
  /// <inheritdoc />
  public ValueTask HandleAsync(SuppressedRouteProbeEvent message, CancellationToken cancellationToken = default) {
    onReceived(message);
    return ValueTask.CompletedTask;
  }
}
