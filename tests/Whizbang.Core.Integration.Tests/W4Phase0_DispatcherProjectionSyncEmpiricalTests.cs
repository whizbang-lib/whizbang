#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Integration.Tests.Generated;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Integration.Tests;

/// <summary>
/// W4 Phase 0 empirical regression-lock: does <see cref="IDispatcher.LocalInvokeAndSyncAsync{TMessage}"/>
/// actually wait for perspectives when called from a real receptor flow inside a DI scope,
/// or does it silently early-return with <see cref="SyncOutcome.NoPendingEvents"/>?
/// </summary>
/// <remarks>
/// <para>
/// Replicates the canonical receptor-emitting-event call shape that downstream
/// consumers exercise when running seed code that needs read-after-write semantics.
/// </para>
/// <para>
/// The test probes the dispatcher's behavior via the <c>onDecisionMade</c> callback. The
/// callback captures <see cref="SyncDecisionContext.DidWait"/> — whether the dispatcher
/// actually awaited perspective completion or returned without waiting.
/// </para>
/// <para>
/// Possible outcomes:
/// <list type="bullet">
///   <item><strong>didWait=true, Outcome=Synced</strong> — framework works correctly; any caller-side polling defense is unnecessary; W4 plan stands down (or shrinks to API cleanup only).</item>
///   <item><strong>didWait=false, Outcome=NoPendingEvents</strong> — framework silently bypassed the wait. Confirms a latent bug; the minimum viable patch is the AsyncLocal fallback in <c>_trackEventForSync</c>.</item>
///   <item><strong>didWait=true, Outcome=TimedOut</strong> — framework tried to wait but never got the signal. Different flavor of bug; needs deeper investigation.</item>
/// </list>
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/projection-sync</docs>
[Category("Integration")]
[Category("W4Phase0")]
public class W4Phase0_DispatcherProjectionSyncEmpiricalTests {

  /// <summary>
  /// A receptor that emits an event when handling a command. Mimics the canonical receptor-emitting-event
  /// pattern: dispatcher.LocalInvokeAndSyncAsync(InitializeSystemManagedListCommand) →
  /// receptor emits SystemManagedListInitializedEvent.
  /// </summary>
  public class _seedingReceptor : IReceptor<_seedTestCommand, _seedTestEvent> {
    public ValueTask<_seedTestEvent> HandleAsync(_seedTestCommand message, CancellationToken cancellationToken = default) {
      var evt = new _seedTestEvent {
        StreamId = message.StreamId,
        Tag = message.Tag,
      };
      return ValueTask.FromResult(evt);
    }
  }

  public sealed record _seedTestCommand : ICommand {
    [StreamId]
    public required Guid StreamId { get; init; }
    public required string Tag { get; init; }
  }

  public sealed record _seedTestEvent : IEvent {
    [StreamId]
    public required Guid StreamId { get; init; }
    public required string Tag { get; init; }
  }

  /// <summary>
  /// CRITICAL TEST: does the dispatcher actually wait for perspectives when invoked from
  /// inside a DI scope, as the in-scope receptor does?
  /// </summary>
  [Test]
  public async Task LocalInvokeAndSyncAsync_FromScopeWithReceptorEmittingEvent_DidWaitAsync() {
    // Arrange: minimal Whizbang surface — Dispatcher + Receptors + sync services.
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    // The IEventCompletionAwaiter + IScopedEventTracker registration that AddWhizbang()
    // applies is mirrored here directly so we exercise the production wiring.
    services.AddSingleton<IEventCompletionAwaiter, EventCompletionAwaiter>();
    services.AddSingleton<ISyncEventTracker, SyncEventTracker>();
    services.AddSingleton<ITrackedEventTypeRegistry, TrackedEventTypeRegistry>();
    services.AddScoped<IScopedEventTracker>(_ => {
      var tracker = new ScopedEventTracker();
      ScopedEventTrackerAccessor.CurrentTracker = tracker;
      return tracker;
    });

    var rootProvider = services.BuildServiceProvider();

    // Create a DI scope to mimic the receptor invocation context. in-scope receptors run
    // inside a scope created by the work coordinator.
    using var scope = rootProvider.CreateScope();

    // Trigger IScopedEventTracker resolution so its factory sets the AsyncLocal. The receptor
    // invocation path normally resolves IEventStore (which then resolves IScopedEventTracker
    // via the SyncTrackingEventStoreDecorator factory). Without that resolution, the AsyncLocal
    // stays null and the wait immediately returns NoPendingEvents — that's the failure mode
    // we want to detect SEPARATELY from any subsequent tracker-state issue.
    _ = scope.ServiceProvider.GetRequiredService<IScopedEventTracker>();

    var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

    SyncDecisionContext? capturedContext = null;
    var command = new _seedTestCommand {
      StreamId = (Guid)TrackedGuid.NewMedo(),
      Tag = "phase-0-empirical-probe",
    };

    // Act: call LocalInvokeAndSyncAsync exactly as seed-code patterns do, with the addition of
    // an onDecisionMade callback that captures whether the wait fired.
    var result = await dispatcher.LocalInvokeAndSyncAsync(
      command,
      timeout: TimeSpan.FromSeconds(2),
      onWaiting: null,
      onDecisionMade: ctx => capturedContext = ctx,
      cancellationToken: CancellationToken.None);

    // Assert: we don't assert any specific outcome — this test is OBSERVATIONAL. The point
    // is to record what the framework actually does in this configuration. Output the
    // findings via assertion messages so they appear in test output for the W4 Phase 0
    // notes file.
    await Assert.That(capturedContext).IsNotNull()
      .Because($"onDecisionMade MUST be invoked; otherwise the dispatcher took an unknown code path. Result was Outcome={result.Outcome}, EventsAwaited={result.EventsAwaited}, Elapsed={result.ElapsedTime.TotalMilliseconds}ms.");

    var didWait = capturedContext!.DidWait;
    var outcome = result.Outcome;
    var eventsAwaited = result.EventsAwaited;

    // Load-bearing claim for Phase 0: the dispatcher actually waited for at least one event
    // (didWait==true AND EventsAwaited>=1). If that holds, the framework works for the consumer
    // seed-code pattern; the proposal's claim is wrong. If didWait==false OR EventsAwaited==0,
    // the wait method bailed out early — that's the silent bug the proposal addresses.
    var conclusion = (didWait, eventsAwaited, outcome) switch {
      (true, > 0, _) => $"FRAMEWORK_WORKS — didWait=true, EventsAwaited={eventsAwaited}, Outcome={outcome}",
      (false, _, SyncOutcome.NoPendingEvents) => $"BUG_CONFIRMED — dispatcher silently returned NoPendingEvents without waiting (Outcome={outcome}, EventsAwaited={eventsAwaited})",
      _ => $"UNEXPECTED — didWait={didWait}, EventsAwaited={eventsAwaited}, Outcome={outcome}",
    };

    await Assert.That(conclusion).StartsWith("FRAMEWORK_WORKS")
      .Because($"PHASE 0 OBSERVATION — {conclusion}, Elapsed={result.ElapsedTime.TotalMilliseconds:F0}ms. " +
               "Test PASSES → framework correctly waited for at least one event; caller-side PollUntilAsync is defensive code that the framework already handles correctly. " +
               "Test FAILS with 'BUG_CONFIRMED' → dispatcher bailed without waiting (the latent bug the W4 proposal addresses). " +
               "Test FAILS with 'UNEXPECTED' → different code path; investigate captured values.");
  }

  /// <summary>
  /// Baseline comparison: manually populate the scoped event tracker (the path the existing
  /// DispatcherLocalInvokeAndSyncTimingTests use) and confirm the wait DOES fire when the
  /// tracker has events. If this baseline passes while the main test above shows the wait
  /// didn't fire, the conclusion is concrete: events emitted via the receptor flow don't
  /// reach the tracker the wait reads from.
  /// </summary>
  [Test]
  public async Task LocalInvokeAndSyncAsync_WithManuallyTrackedEvent_DoesWaitAsync() {
    // Arrange — same as above plus a fake awaiter we can observe.
    var fakeAwaiter = new _capturingEventCompletionAwaiter();
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddSingleton<IEventCompletionAwaiter>(fakeAwaiter);
    services.AddSingleton<ISyncEventTracker, SyncEventTracker>();
    services.AddSingleton<ITrackedEventTypeRegistry, TrackedEventTypeRegistry>();
    services.AddScoped<IScopedEventTracker>(_ => {
      var tracker = new ScopedEventTracker();
      ScopedEventTrackerAccessor.CurrentTracker = tracker;
      return tracker;
    });

    var rootProvider = services.BuildServiceProvider();
    using var scope = rootProvider.CreateScope();
    var scopedTracker = scope.ServiceProvider.GetRequiredService<IScopedEventTracker>();

    // Manually inject a tracked event so the wait's "no events" early-return is bypassed.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var eventId = (Guid)TrackedGuid.NewMedo();
    scopedTracker.TrackEmittedEvent(streamId, typeof(_seedTestEvent), eventId);

    var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
    var command = new _seedTestCommand { StreamId = streamId, Tag = "baseline" };

    SyncDecisionContext? capturedContext = null;

    // Act
    var result = await dispatcher.LocalInvokeAndSyncAsync(
      command,
      timeout: TimeSpan.FromSeconds(2),
      onWaiting: null,
      onDecisionMade: ctx => capturedContext = ctx,
      cancellationToken: CancellationToken.None);

    // Assert: with a manually-populated tracker, the wait MUST fire.
    await Assert.That(capturedContext).IsNotNull();
    await Assert.That(capturedContext!.DidWait).IsTrue()
      .Because($"BASELINE: when the tracker is manually populated, the dispatcher MUST hit the wait path. Actual: didWait={capturedContext.DidWait}, Outcome={result.Outcome}, EventsAwaited={result.EventsAwaited}.");
    await Assert.That(fakeAwaiter.WasCalled).IsTrue()
      .Because("The IEventCompletionAwaiter mock MUST receive a call when the tracker has events. If false, the dispatcher's wait method skipped the awaiter entirely.");
    // At minimum the manually-tracked event must be present. The receptor's emitted event
    // also lands in the tracker (proven by the main test) so the count is ≥1, not == 1.
    await Assert.That(fakeAwaiter.LastEventIds.Count).IsGreaterThanOrEqualTo(1)
      .Because($"Awaiter received {fakeAwaiter.LastEventIds.Count} event(s). The manually-tracked event MUST be among them.");
    await Assert.That(fakeAwaiter.LastEventIds).Contains(eventId)
      .Because("The manually-injected event MUST be in the awaiter's event list — proves the wait method pulls from the same tracker the test wrote into.");
  }

  /// <summary>
  /// Capturing test double for <see cref="IEventCompletionAwaiter"/>. Records whether the
  /// dispatcher called WaitForEventsAsync and with which event IDs.
  /// </summary>
  private sealed class _capturingEventCompletionAwaiter : IEventCompletionAwaiter {
    public bool WasCalled { get; private set; }
    public List<Guid> LastEventIds { get; private set; } = [];

    public Guid AwaiterId { get; } = Guid.NewGuid();

    public Task<bool> WaitForEventsAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout, CancellationToken cancellationToken) {
      WasCalled = true;
      LastEventIds = [.. eventIds];
      return Task.FromResult(true);
    }

    public bool AreEventsFullyProcessed(IReadOnlyList<Guid> eventIds) => true;
  }
}
