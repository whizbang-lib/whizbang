using System.Collections.Concurrent;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Test receptor that counts perspective completions and signals when all expected perspectives have completed.
/// Used for deterministic test synchronization when one event triggers multiple perspectives.
/// </summary>
/// <typeparam name="TEvent">The event type to wait for.</typeparam>
[FireAt(LifecycleStage.PostPerspectiveInline)]
public sealed class CountingPerspectiveReceptor<TEvent> : IReceptor<TEvent>
  where TEvent : IEvent {

  private readonly TaskCompletionSource<bool> _completionSource;
  private readonly ConcurrentBag<string> _completedPerspectives;
  private readonly int _expectedCount;
  private int _completionCount = 0;

  public CountingPerspectiveReceptor(
    TaskCompletionSource<bool> completionSource,
    ConcurrentBag<string> completedPerspectives,
    int expectedCount) {

    _completionSource = completionSource ?? throw new ArgumentNullException(nameof(completionSource));
    _completedPerspectives = completedPerspectives ?? throw new ArgumentNullException(nameof(completedPerspectives));
    _expectedCount = expectedCount;

    Console.WriteLine($"[CountingReceptor.ctor] Created receptor expecting {expectedCount} perspective completions");
  }

  public ValueTask HandleAsync(TEvent message, CancellationToken cancellationToken = default) {
    // Use DI to get lifecycle context (if available)
    // For now, we'll track by counting invocations
    var currentCount = Interlocked.Increment(ref _completionCount);

    Console.WriteLine($"[CountingReceptor] Perspective completed ({currentCount}/{_expectedCount}) for event {typeof(TEvent).Name}");

    // Track perspective name if we can extract it
    _completedPerspectives.Add($"Perspective#{currentCount}");

    // Signal completion when all perspectives have processed
    if (currentCount >= _expectedCount) {
      Console.WriteLine($"[CountingReceptor] ALL {_expectedCount} perspectives completed! Signaling completion.");
      _completionSource.TrySetResult(true);
    }

    return ValueTask.CompletedTask;
  }
}
