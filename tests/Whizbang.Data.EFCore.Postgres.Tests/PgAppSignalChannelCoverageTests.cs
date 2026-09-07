using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage-round tests for <see cref="PgAppSignalChannel"/> paths that
/// <see cref="PgAppSignalChannelIntegrationTests"/> doesn't reach: a subscribed handler throwing
/// during fan-out, and idempotent disposal of the handle <see cref="PgAppSignalChannel.Subscribe"/>
/// returns. None of these need a real Postgres connection — dispatch runs entirely in memory once
/// a subscription is registered, so a capturing fake <see cref="ISharedNotifyConnection"/> stands
/// in for the shared connection the same way <c>PgAppSignalChannelIntegrationTests.NoOpSharedConnection</c>
/// does, except this one keeps the registered <see cref="INotifySubscription"/> so the test can
/// drive <c>OnNotification</c> directly.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgAppSignalChannel.cs</code-under-test>
[Category("Shard1")]
public class PgAppSignalChannelCoverageTests {

  private sealed class CapturingSharedConnection : ISharedNotifyConnection {
    public INotifySubscription? Captured { get; private set; }
    public IDisposable Subscribe(INotifySubscription subscription) {
      Captured = subscription;
      return new _noOpDisposable();
    }
    private sealed class _noOpDisposable : IDisposable {
      public void Dispose() { }
    }
  }

  /// <summary>Captures every message logged, at any level.</summary>
  private sealed class CapturingLogger : ILogger<PgAppSignalChannel> {
    private readonly Lock _lock = new();
    private readonly List<string> _messages = [];
    public List<string> Messages { get { lock (_lock) { return [.. _messages]; } } }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_lock) { _messages.Add(formatter(state, exception)); }
    }
  }

  private static PgAppSignalChannel _channel(ISharedNotifyConnection shared, ILogger<PgAppSignalChannel>? logger = null) =>
    new(
      Options.Create(new WhizbangNotificationOptions()),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      shared,
      logger ?? new CapturingLogger());

  // Fan-out to N handlers means one broken handler must not take down delivery to the rest, and
  // an operator needs the failure to be visible somewhere -- a silently swallowed throw here
  // would look exactly like an idle subscriber that simply never got called.
  [Test]
  public async Task OnNotification_WhenAHandlerThrows_LogsItAndStillDeliversToTheOtherHandlersAsync() {
    var shared = new CapturingSharedConnection();
    var logger = new CapturingLogger();
    var channel = _channel(shared, logger);
    var secondHandlerRan = false;

    using var broken = channel.Subscribe("topic_with_a_bad_handler", (_, _) => throw new InvalidOperationException("boom"));
    using var healthy = channel.Subscribe("topic_with_a_bad_handler", (_, _) => {
      secondHandlerRan = true;
      return Task.CompletedTask;
    });

    shared.Captured!.OnNotification("payload");

    await Assert.That(secondHandlerRan).IsTrue()
      .Because("one handler throwing must not stop delivery to the other handlers subscribed to the same topic");
    await Assert.That(logger.Messages.Any(m => m.Contains("topic_with_a_bad_handler", StringComparison.Ordinal))).IsTrue()
      .Because("a swallowed handler exception with no log line is indistinguishable from a handler nobody called");
  }

  // A caller path that disposes a subscription handle twice (redundant cleanup during shutdown,
  // or a retry that re-runs teardown) must not attempt a second removal against a topic another
  // Subscribe may have already re-created under the same name.
  [Test]
  public async Task Subscribe_DisposingTheReturnedHandleTwice_IsIdempotentAsync() {
    var shared = new CapturingSharedConnection();
    var channel = _channel(shared);
    var sub = channel.Subscribe("topic", (_, _) => Task.CompletedTask);

    sub.Dispose();

    await Assert.That(() => sub.Dispose()).ThrowsNothing()
      .Because("a second Dispose on the same handle must be a no-op, not a second attempt to unregister an already-removed handler");
  }

  /// <summary>
  /// Concurrent disposal of one subscription handle must stay a no-op rather than double-unregister.
  /// </summary>
  /// <remarks>
  /// <see cref="PgAppSignalChannel"/>'s private <c>HandlerSubscription.Dispose()</c> guards
  /// against double-disposal with a plain <c>bool</c> field, unsynchronized. Two threads that
  /// both observe it as unset before either sets it can both proceed into the internal
  /// <c>_removeHandler</c> call for the same topic; <c>_removeHandler</c>'s own "topic missing"
  /// guard is what makes the losing call a safe no-op rather than tearing down a fresh
  /// <see cref="Whizbang.Core.Notifications.INotifySubscription"/> registration a concurrent
  /// Subscribe already re-created under the same name. This test forces that race with a
  /// <see cref="Barrier"/> so every racer disposes as close to simultaneously as possible; the
  /// assertions hold regardless of whether the race actually lands on the guard this round, so
  /// the test cannot flake, but it also cannot guarantee that exact line executes on every run.
  /// </remarks>
  [Test]
  public async Task Subscribe_DisposedConcurrentlyFromManyThreads_NeverThrowsAndLeavesTheTopicReusableAsync() {
    var shared = new CapturingSharedConnection();
    var channel = _channel(shared);
    var sub = channel.Subscribe("race_topic", (_, _) => Task.CompletedTask);

    const int racers = 16;
    using var barrier = new Barrier(racers);
    var threads = Enumerable.Range(0, racers).Select(_ => new Thread(() => {
      barrier.SignalAndWait();
      sub.Dispose();
    })).ToArray();
    foreach (var t in threads) { t.Start(); }
    foreach (var t in threads) { t.Join(); }

    // Whichever thread's removal actually tore the topic down, a fresh Subscribe under the same
    // name afterward must register and deliver normally -- a corrupted intermediate state would
    // silently drop this handler instead.
    var resubscribedHandlerRan = false;
    using var after = channel.Subscribe("race_topic", (_, _) => {
      resubscribedHandlerRan = true;
      return Task.CompletedTask;
    });
    shared.Captured!.OnNotification("ping");

    await Assert.That(resubscribedHandlerRan).IsTrue()
      .Because("a topic left in a half-torn-down state by a racing double dispose would silently drop the next subscriber");
  }
}
