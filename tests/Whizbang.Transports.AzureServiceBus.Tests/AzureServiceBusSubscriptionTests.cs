using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// The subscription handle a caller holds onto after subscribing.
/// </summary>
/// <remarks>
/// Pause and resume deliberately do NOT stop the underlying processor — they flip a flag the
/// message handler reads, and the handler abandons instead of processing. Stopping and restarting
/// the processor causes handler re-registration problems, so the cheap-looking implementation is
/// the deliberate one.
///
/// <para>
/// The disconnect notification is the other half: a subscription that drops is how a consumer
/// learns it has stopped receiving, and raising it after disposal would deliver a callback to a
/// caller that has already torn down its handler.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusSubscription.cs</code-under-test>
public class AzureServiceBusSubscriptionTests {

  /// <summary>A processor that never runs, so disposal has nothing live to stop.</summary>
  private sealed class InertProcessor : ServiceBusProcessor {
    public override Task StartProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public override Task StopProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private sealed class InertSessionProcessor : ServiceBusSessionProcessor {
    private readonly InertProcessor _inner = new();
    protected override ServiceBusProcessor InnerProcessor => _inner;
    public override Task StartProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public override Task StopProcessingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private sealed class CapturingLogger : ILogger {
    private readonly Lock _lock = new();
    private readonly List<string> _messages = [];
    public List<string> Messages { get { lock (_lock) { return [.. _messages]; } } }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_lock) { _messages.Add(formatter(state, exception)); }
    }
  }

  private static AzureServiceBusSubscription _subscription(ILogger? logger = null)
    => new(new InertProcessor(), logger ?? NullLogger.Instance);

  // ============================================================
  // Construction
  // ============================================================

  [Test]
  public async Task ASubscriptionStartsActiveAsync() {
    using var subscription = _subscription();

    await Assert.That(subscription.IsActive).IsTrue();
  }

  [Test]
  public async Task Constructor_RejectsANullProcessorAsync() {
    await Assert.That(() => new AzureServiceBusSubscription((ServiceBusProcessor)null!, NullLogger.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new AzureServiceBusSubscription((ServiceBusSessionProcessor)null!, NullLogger.Instance))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_RejectsANullLoggerAsync() {
    await Assert.That(() => new AzureServiceBusSubscription(new InertProcessor(), null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ASessionSubscriptionIsAlsoActiveAsync() {
    using var subscription = new AzureServiceBusSubscription(new InertSessionProcessor(), NullLogger.Instance);

    await Assert.That(subscription.IsActive).IsTrue();
  }

  // ============================================================
  // Pause and resume
  // ============================================================

  [Test]
  public async Task Pause_MakesTheSubscriptionInactiveAsync() {
    using var subscription = _subscription();

    await subscription.PauseAsync();

    await Assert.That(subscription.IsActive).IsFalse();
  }

  [Test]
  public async Task Pause_IsIdempotentAsync() {
    // Pausing an already-paused subscription is ordinary — a health responder and an operator
    // command can both ask. The guard is what keeps the second call from re-logging and
    // re-announcing a state change that did not happen.
    var logger = new CapturingLogger();
    using var subscription = _subscription(logger);

    await subscription.PauseAsync();
    var afterFirst = logger.Messages.Count;
    await subscription.PauseAsync();

    await Assert.That(subscription.IsActive).IsFalse();
    await Assert.That(logger.Messages.Count).IsEqualTo(afterFirst)
      .Because("the second pause changed nothing, so it must not report a transition");
  }

  [Test]
  public async Task Resume_MakesTheSubscriptionActiveAgainAsync() {
    using var subscription = _subscription();
    await subscription.PauseAsync();

    await subscription.ResumeAsync();

    await Assert.That(subscription.IsActive).IsTrue();
  }

  [Test]
  public async Task Resume_OnAnActiveSubscriptionIsANoOpAsync() {
    var logger = new CapturingLogger();
    using var subscription = _subscription(logger);

    await subscription.ResumeAsync();

    await Assert.That(subscription.IsActive).IsTrue();
    await Assert.That(logger.Messages).IsEmpty();
  }

  [Test]
  public async Task PauseAndResume_CanCycleAsync() {
    using var subscription = _subscription();

    for (var i = 0; i < 3; i++) {
      await subscription.PauseAsync();
      await Assert.That(subscription.IsActive).IsFalse();
      await subscription.ResumeAsync();
      await Assert.That(subscription.IsActive).IsTrue();
    }
  }

  // ============================================================
  // Disconnect notification
  // ============================================================

  [Test]
  public async Task RaiseDisconnected_NotifiesTheSubscriberAsync() {
    // This is how a consumer learns it has stopped receiving. Without it a dropped subscription
    // is indistinguishable from a quiet topic.
    using var subscription = _subscription();
    SubscriptionDisconnectedEventArgs? seen = null;
    subscription.OnDisconnected += (_, e) => seen = e;

    subscription.RaiseDisconnected("connection lost", new InvalidOperationException("boom"));

    await Assert.That(seen).IsNotNull();
    await Assert.That(seen!.Reason).IsEqualTo("connection lost");
    await Assert.That(seen.Exception).IsNotNull();
  }

  [Test]
  public async Task RaiseDisconnected_MarksTheDropAsNotApplicationInitiatedAsync() {
    // The flag is what tells the recovery path whether to resubscribe. Reporting a broker-side
    // drop as application-initiated would leave the consumer permanently unsubscribed.
    using var subscription = _subscription();
    SubscriptionDisconnectedEventArgs? seen = null;
    subscription.OnDisconnected += (_, e) => seen = e;

    subscription.RaiseDisconnected("connection lost", exception: null);

    await Assert.That(seen!.IsApplicationInitiated).IsFalse();
  }

  [Test]
  public async Task RaiseDisconnected_ReportsTheReasonAsync() {
    var logger = new CapturingLogger();
    using var subscription = _subscription(logger);

    subscription.RaiseDisconnected("connection lost", exception: null);

    await Assert.That(logger.Messages.Any(m => m.Contains("connection lost", StringComparison.Ordinal)))
      .IsTrue();
  }

  [Test]
  public async Task RaiseDisconnected_WithNoSubscriber_IsHarmlessAsync() {
    // The transport raises this whether or not anyone attached a handler.
    using var subscription = _subscription();

    subscription.RaiseDisconnected("connection lost", exception: null);

    await Assert.That(subscription.IsActive).IsTrue();
  }

  [Test]
  public async Task RaiseDisconnected_AfterDisposal_IsSilentAsync() {
    // The caller has already torn down whatever the handler touched, so delivering the callback
    // now would run it against disposed state.
    var subscription = _subscription();
    var raised = 0;
    subscription.OnDisconnected += (_, _) => raised++;
    subscription.Dispose();

    subscription.RaiseDisconnected("connection lost", exception: null);

    await Assert.That(raised).IsEqualTo(0)
      .Because("the subscriber has torn down — the callback would run against disposed state");
  }

  // ============================================================
  // Disposal
  // ============================================================

  [Test]
  public async Task Dispose_DeactivatesTheSubscriptionAsync() {
    var subscription = _subscription();

    subscription.Dispose();

    await Assert.That(subscription.IsActive).IsFalse();
  }

  [Test]
  public async Task Dispose_IsIdempotentAsync() {
    // A `using` plus an explicit close in the caller's shutdown is an ordinary shape, and the
    // second pass must not close an already-closed processor.
    var subscription = _subscription();

    subscription.Dispose();
    subscription.Dispose();

    await Assert.That(subscription.IsActive).IsFalse();
  }

  [Test]
  public async Task Dispose_OfASessionSubscriptionIsCleanAsync() {
    var subscription = new AzureServiceBusSubscription(new InertSessionProcessor(), NullLogger.Instance);

    subscription.Dispose();
    subscription.Dispose();

    await Assert.That(subscription.IsActive).IsFalse();
  }
}
