using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Implementation of <see cref="IEventCompletionAwaiter"/> that waits for events
/// to be fully processed by all perspectives.
/// </summary>
/// <remarks>
/// <para>
/// Delegates to <see cref="ISyncEventTracker.WaitForAllPerspectivesAsync"/> which waits
/// until ALL perspectives have processed the events.
/// </para>
/// <para>
/// This differs from <see cref="PerspectiveSyncAwaiter"/> which uses
/// <see cref="ISyncEventTracker.WaitForPerspectiveEventsAsync"/> to wait for a
/// SPECIFIC perspective.
/// </para>
/// </remarks>
/// <docs>core-concepts/perspectives/event-completion</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/EventCompletionAwaiterTests.cs</tests>
public sealed class EventCompletionAwaiter : IEventCompletionAwaiter {
  /// <inheritdoc />
  public Guid AwaiterId { get; } = TrackedGuid.NewMedo();

  private readonly ISyncEventTracker _syncEventTracker;

  /// <summary>
  /// Initializes a new instance of the <see cref="EventCompletionAwaiter"/> class.
  /// </summary>
  /// <param name="syncEventTracker">The sync event tracker to use for waiting.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="syncEventTracker"/> is null.</exception>
  public EventCompletionAwaiter(ISyncEventTracker syncEventTracker) {
    _syncEventTracker = syncEventTracker ?? throw new ArgumentNullException(nameof(syncEventTracker));
  }

  /// <inheritdoc />
  public Task<bool> WaitForEventsAsync(
      IReadOnlyList<Guid> eventIds,
      TimeSpan timeout,
      CancellationToken cancellationToken = default) {
    // Use WaitForAllPerspectivesAsync - waits until ALL perspectives have processed
    return _syncEventTracker.WaitForAllPerspectivesAsync(eventIds, timeout, AwaiterId, cancellationToken);
  }

  /// <inheritdoc />
  public bool AreEventsFullyProcessed(IReadOnlyList<Guid> eventIds) {
    if (eventIds is null || eventIds.Count == 0) {
      return true;
    }

    var trackedIds = _syncEventTracker.GetAllTrackedEventIds();
    return !eventIds.Any(id => trackedIds.Contains(id));
  }
}
