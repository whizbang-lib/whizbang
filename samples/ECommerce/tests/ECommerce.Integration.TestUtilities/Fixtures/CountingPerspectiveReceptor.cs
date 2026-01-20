using System.Collections.Concurrent;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Test receptor that counts perspective completions per stream and signals when all expected perspective-stream pairs have completed.
/// Used for deterministic test synchronization when multiple events trigger perspectives across different streams.
/// Tracks (perspectiveName, streamId) pairs to support multiple invocations of the same perspective for different events/streams.
/// </summary>
/// <typeparam name="TEvent">The event type to wait for.</typeparam>
[FireAt(LifecycleStage.PostPerspectiveInline)]
public sealed class CountingPerspectiveReceptor<TEvent> : IReceptor<TEvent>, IAcceptsLifecycleContext
  where TEvent : IEvent {

  private readonly TaskCompletionSource<bool> _completionSource;
  private readonly ConcurrentDictionary<string, byte> _completedPerspectives; // Changed from ConcurrentBag to fix race condition
  private readonly int _expectedCount;
  private int _completionCount = 0;
  private ILifecycleContext? _context;

  public CountingPerspectiveReceptor(
    TaskCompletionSource<bool> completionSource,
    ConcurrentDictionary<string, byte> completedPerspectives, // Changed from ConcurrentBag to fix race condition
    int expectedCount) {

    _completionSource = completionSource ?? throw new ArgumentNullException(nameof(completionSource));
    _completedPerspectives = completedPerspectives ?? throw new ArgumentNullException(nameof(completedPerspectives));
    _expectedCount = expectedCount;

    Console.WriteLine($"[CountingReceptor.ctor] Created receptor expecting {expectedCount} perspective completions");
  }

  /// <inheritdoc/>
  public void SetLifecycleContext(ILifecycleContext context) {
    _context = context;
  }

  public ValueTask HandleAsync(TEvent message, CancellationToken cancellationToken = default) {
    // Get perspective name and stream ID from lifecycle context
    var perspectiveName = _context?.PerspectiveType?.Name ?? "Unknown";
    var streamId = _context?.StreamId?.ToString() ?? "Unknown";

    Console.WriteLine($"[CountingReceptor] Perspective '{perspectiveName}' completed for event {typeof(TEvent).Name} on stream {streamId}");

    // Track (perspectiveName, streamId) pairs to count per-stream invocations
    // This allows counting multiple events for the same perspective (e.g., 2 ProductCreatedEvents → 2 InventoryLevelsPerspective invocations)
    var key = $"{perspectiveName}:{streamId}";

    // CRITICAL: Use TryAdd for atomic check-and-add to prevent race condition
    // ConcurrentBag.Contains + Add is NOT atomic and causes duplicate counting under concurrent execution
    if (_completedPerspectives.TryAdd(key, 0)) {
      var currentCount = Interlocked.Increment(ref _completionCount);

      Console.WriteLine($"[CountingReceptor] Unique perspective-stream count: {currentCount}/{_expectedCount}");

      // Signal completion when all expected perspective-stream pairs have processed
      if (currentCount >= _expectedCount) {
        Console.WriteLine($"[CountingReceptor] ALL {_expectedCount} perspective-stream pairs completed! Signaling completion.");
        _completionSource.TrySetResult(true);
      }
    } else {
      Console.WriteLine($"[CountingReceptor] Perspective-stream '{key}' already counted (duplicate invocation)");
    }

    return ValueTask.CompletedTask;
  }
}
