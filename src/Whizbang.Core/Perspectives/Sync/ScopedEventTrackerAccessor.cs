namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Provides ambient access to the current scope's <see cref="IScopedEventTracker"/>.
/// </summary>
/// <remarks>
/// <para>
/// This accessor uses <see cref="AsyncLocal{T}"/> to store the scoped tracker,
/// making it accessible from singleton services like <see cref="Dispatcher"/>
/// that cannot have scoped dependencies injected directly.
/// </para>
/// <para>
/// The tracker is automatically set when <see cref="IScopedEventTracker"/> is resolved
/// from a scope, and cleared when the scope is disposed.
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> AsyncLocal ensures each async execution context
/// has its own tracker instance, providing proper scope isolation.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync#scoped-tracker-accessor</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/ScopedEventTrackerAccessorTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSyncModeBehaviorTests.cs:AllProjections_NoScopedTracker_FastReturnAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSyncModeBehaviorTests.cs:AllProjections_WithEventsTracked_InvokesAwaiterWithInfiniteTimeoutAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherPerspectiveSyncCoverageTests.cs:LocalInvokeAsync_WithWaitForPerspectives_NoScopedTracker_ReturnsNormallyAsync</tests>
public static class ScopedEventTrackerAccessor {
  private static readonly AsyncLocal<IScopedEventTracker?> _current = new();

  /// <summary>
  /// Gets or sets the current scope's event tracker.
  /// </summary>
  /// <remarks>
  /// Returns <c>null</c> if called outside of a scope or if no tracker has been set.
  /// Setting to <c>null</c> clears the current tracker.
  /// </remarks>
  public static IScopedEventTracker? CurrentTracker {
    get => _current.Value;
    set => _current.Value = value;
  }
}

/// <summary>
/// The framework's null default for <see cref="IScopedEventTracker"/>: tracks nothing and reports
/// <see cref="IScopedEventTracker.IsAvailable"/> false. Used where a singleton-lifetime store cannot
/// hold a request-scoped tracker.
/// </summary>
public sealed class NullScopedEventTracker : IScopedEventTracker, INullDefault {
  private NullScopedEventTracker() { }

  /// <summary>The shared instance.</summary>
  public static NullScopedEventTracker Instance { get; } = new();

  /// <inheritdoc />
  public bool IsAvailable => false;

  /// <inheritdoc />
  public void TrackEmittedEvent(Guid streamId, Type eventType, Guid eventId) { }

  /// <inheritdoc />
  public IReadOnlyList<TrackedEvent> GetEmittedEvents() => [];

  /// <inheritdoc />
  public IReadOnlyList<TrackedEvent> GetEmittedEvents(SyncFilterNode filter) => [];

  /// <inheritdoc />
  public bool AreAllProcessed(SyncFilterNode filter, IReadOnlySet<Guid> processedEventIds) => true;
}

/// <summary>
/// Forwards to whichever tracker <see cref="ScopedEventTrackerAccessor.CurrentTracker"/> holds for the
/// current async flow, so a singleton-lifetime event store can still wait on the events its caller's
/// scope emitted. Reports <see cref="IScopedEventTracker.IsAvailable"/> false outside a tracked scope.
/// </summary>
public sealed class AmbientScopedEventTracker : IScopedEventTracker {
  private AmbientScopedEventTracker() { }

  /// <summary>The shared instance.</summary>
  public static AmbientScopedEventTracker Instance { get; } = new();

  /// <inheritdoc />
  public bool IsAvailable => ScopedEventTrackerAccessor.CurrentTracker is not null;

  /// <inheritdoc />
  public void TrackEmittedEvent(Guid streamId, Type eventType, Guid eventId) =>
    ScopedEventTrackerAccessor.CurrentTracker?.TrackEmittedEvent(streamId, eventType, eventId);

  /// <inheritdoc />
  public IReadOnlyList<TrackedEvent> GetEmittedEvents() => ScopedEventTrackerAccessor.CurrentTracker?.GetEmittedEvents() ?? [];

  /// <inheritdoc />
  public IReadOnlyList<TrackedEvent> GetEmittedEvents(SyncFilterNode filter) => ScopedEventTrackerAccessor.CurrentTracker?.GetEmittedEvents(filter) ?? [];

  /// <inheritdoc />
  public bool AreAllProcessed(SyncFilterNode filter, IReadOnlySet<Guid> processedEventIds) =>
    ScopedEventTrackerAccessor.CurrentTracker?.AreAllProcessed(filter, processedEventIds) ?? true;
}

