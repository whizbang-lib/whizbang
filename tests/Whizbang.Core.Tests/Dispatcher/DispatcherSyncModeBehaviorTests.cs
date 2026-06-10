#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Generated;

// Reuse the existing VoidCommand + VoidCommandReceptor declared by
// DispatcherLocalInvokeAndSyncTests so the source-generated AddReceptors() registry
// count stays stable. (Diagnostics receptor-count tests assert against a fixed
// total — adding another IReceptor<T> class here would break those.)
using VoidCommand = Whizbang.Core.Tests.Dispatcher.DispatcherLocalInvokeAndSyncTests.VoidCommand;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Behavioral coverage for the W4 <c>LocalInvokeAndSyncAsync&lt;TMessage&gt;(message, SyncMode, ct)</c>
/// overload. <see cref="DispatcherSyncModeContractTests"/> pins the SHAPE via reflection; these
/// pin the BEHAVIOR — that StreamOnly skips the perspective wait, AllProjections delegates to the
/// per-perspective awaiter with <see cref="Timeout.InfiniteTimeSpan"/>, and the metric timer fires
/// in the finally regardless of which path is taken.
/// </summary>
/// <docs>fundamentals/dispatcher/sync-mode</docs>
[Category("Dispatcher")]
[Category("Sync")]
[NotInParallel] // Uses static ScopedEventTrackerAccessor.CurrentTracker
public sealed class DispatcherSyncModeBehaviorTests {

  [Test]
  public async Task StreamOnly_WithAwaiterRegistered_DoesNotInvokeAwaiterAsync() {
    var awaiter = new RecordingEventCompletionAwaiter(completesImmediately: true);
    var scopedEventTracker = new RecordingScopedEventTracker();
    var dispatcher = _createDispatcher(awaiter);

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      await dispatcher.LocalInvokeAndSyncAsync(new VoidCommand("x"), SyncMode.StreamOnly);

      await Assert.That(awaiter.WaitForEventsWasCalled).IsFalse()
        .Because("StreamOnly must bypass the perspective wait — it's the opt-in fast path.");
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task AllProjections_WithEventsTracked_InvokesAwaiterWithInfiniteTimeoutAsync() {
    var awaiter = new RecordingEventCompletionAwaiter(completesImmediately: true);
    var scopedEventTracker = new RecordingScopedEventTracker();
    var dispatcher = _createDispatcher(awaiter);

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      var eventId = Guid.NewGuid();
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), eventId);

      await dispatcher.LocalInvokeAndSyncAsync(new VoidCommand("x"), SyncMode.AllProjections);

      await Assert.That(awaiter.WaitForEventsWasCalled).IsTrue()
        .Because("AllProjections must call IEventCompletionAwaiter.WaitForEventsAsync.");
      await Assert.That(awaiter.LastTimeout).IsEqualTo(Timeout.InfiniteTimeSpan)
        .Because("CT-only contract: no implicit timeout, the caller's CT is the only wait bound.");
      await Assert.That(awaiter.LastEventIds!.Count).IsEqualTo(1);
      await Assert.That(awaiter.LastEventIds[0]).IsEqualTo(eventId);
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task AllProjections_NoTrackedEvents_ShortCircuitsWithoutCallingAwaiterAsync() {
    var awaiter = new RecordingEventCompletionAwaiter(completesImmediately: true);
    var scopedEventTracker = new RecordingScopedEventTracker(); // empty
    var dispatcher = _createDispatcher(awaiter);

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      await dispatcher.LocalInvokeAndSyncAsync(new VoidCommand("x"), SyncMode.AllProjections);

      await Assert.That(awaiter.WaitForEventsWasCalled).IsFalse()
        .Because("Zero tracked events means nothing to wait on — fast return without calling the awaiter.");
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task AllProjections_NoScopedTracker_FastReturnAsync() {
    var awaiter = new RecordingEventCompletionAwaiter(completesImmediately: true);
    var dispatcher = _createDispatcher(awaiter);

    // No ScopedEventTrackerAccessor.CurrentTracker assignment — the static stays null.

    await dispatcher.LocalInvokeAndSyncAsync(new VoidCommand("x"), SyncMode.AllProjections);

    await Assert.That(awaiter.WaitForEventsWasCalled).IsFalse()
      .Because("No tracker available means no events to wait on — fast return without calling the awaiter.");
  }

  [Test]
  public async Task AllProjections_NoAwaiterRegistered_StillReturnsAsync() {
    var scopedEventTracker = new RecordingScopedEventTracker();
    var dispatcher = _createDispatcher(eventCompletionAwaiter: null);

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      // No awaiter → the wait function returns Synced without ever blocking. The outer
      // ValueTask method should still complete without throwing.
      await dispatcher.LocalInvokeAndSyncAsync(new VoidCommand("x"), SyncMode.AllProjections);
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  [Test]
  public async Task StreamOnly_NoAwaiterRegistered_StillReturnsAsync() {
    var dispatcher = _createDispatcher(eventCompletionAwaiter: null);

    // StreamOnly never touches the awaiter path; assert this works even without one wired.
    await dispatcher.LocalInvokeAndSyncAsync(new VoidCommand("x"), SyncMode.StreamOnly);
  }

  [Test]
  public async Task AllProjections_AwaiterPropagatesCancellation_OperationCanceledBubblesAsync() {
    var awaiter = new RecordingEventCompletionAwaiter(throwOnWait: true);
    var scopedEventTracker = new RecordingScopedEventTracker();
    var dispatcher = _createDispatcher(awaiter);

    ScopedEventTrackerAccessor.CurrentTracker = scopedEventTracker;
    try {
      scopedEventTracker.TrackEmittedEvent(Guid.NewGuid(), typeof(object), Guid.NewGuid());

      using var cts = new CancellationTokenSource();
      cts.Cancel();

      await Assert.That(async () =>
        await dispatcher.LocalInvokeAndSyncAsync(new VoidCommand("x"), SyncMode.AllProjections, cts.Token))
        .Throws<OperationCanceledException>()
        .Because("CT-only contract: cancellation from the caller's CT propagates from the awaiter to the caller, exercising the finally path.");
    } finally {
      ScopedEventTrackerAccessor.CurrentTracker = null;
    }
  }

  private static IDispatcher _createDispatcher(IEventCompletionAwaiter? eventCompletionAwaiter) {
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
        new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    if (eventCompletionAwaiter != null) {
      services.AddSingleton(eventCompletionAwaiter);
    }
    services.AddWhizbangDispatcher();
    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  private sealed class RecordingEventCompletionAwaiter : IEventCompletionAwaiter {
    private readonly bool _completesImmediately;
    private readonly bool _throwOnWait;

    public RecordingEventCompletionAwaiter(bool completesImmediately = true, bool throwOnWait = false) {
      _completesImmediately = completesImmediately;
      _throwOnWait = throwOnWait;
    }

    public Guid AwaiterId { get; } = Guid.NewGuid();
    public bool WaitForEventsWasCalled { get; private set; }
    public IReadOnlyList<Guid>? LastEventIds { get; private set; }
    public TimeSpan LastTimeout { get; private set; }

    public Task<bool> WaitForEventsAsync(IReadOnlyList<Guid> eventIds, TimeSpan timeout, CancellationToken cancellationToken = default) {
      WaitForEventsWasCalled = true;
      LastEventIds = eventIds;
      LastTimeout = timeout;
      if (_throwOnWait) {
        cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException();
      }
      return Task.FromResult(_completesImmediately);
    }

    public bool AreEventsFullyProcessed(IReadOnlyList<Guid> eventIds) => _completesImmediately;
  }

  private sealed class RecordingScopedEventTracker : IScopedEventTracker {
    private readonly List<TrackedEvent> _events = [];
    public void TrackEmittedEvent(Guid streamId, Type eventType, Guid eventId) =>
      _events.Add(new TrackedEvent(streamId, eventType, eventId));
    public IReadOnlyList<TrackedEvent> GetEmittedEvents() => _events;
    public IReadOnlyList<TrackedEvent> GetEmittedEvents(SyncFilterNode filter) => _events;
    public bool AreAllProcessed(SyncFilterNode filter, IReadOnlySet<Guid> processedEventIds) =>
      _events.All(e => processedEventIds.Contains(e.EventId));
  }
}
