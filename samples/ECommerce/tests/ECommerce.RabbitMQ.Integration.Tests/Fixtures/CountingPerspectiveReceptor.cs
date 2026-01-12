using System.Collections.Concurrent;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace ECommerce.RabbitMQ.Integration.Tests.Fixtures;

/// <summary>
/// Test receptor that counts perspective completions and signals when all expected perspectives have completed.
/// Used for deterministic test synchronization when one event triggers multiple perspectives.
/// </summary>
/// <typeparam name="TEvent">The event type to wait for.</typeparam>
[FireAt(LifecycleStage.PostPerspectiveInline)]
public sealed class CountingPerspectiveReceptor<TEvent> : IReceptor<TEvent>, IAcceptsLifecycleContext
  where TEvent : IEvent {

  private readonly TaskCompletionSource<bool> _completionSource;
  private readonly ConcurrentBag<string> _completedPerspectives;
  private readonly int _expectedCount;
  private int _completionCount = 0;
  private ILifecycleContext? _context;

  public CountingPerspectiveReceptor(
    TaskCompletionSource<bool> completionSource,
    ConcurrentBag<string> completedPerspectives,
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
    // Get perspective name from lifecycle context
    var perspectiveName = _context?.PerspectiveName ?? "Unknown";

    Console.WriteLine($"[CountingReceptor] Perspective '{perspectiveName}' completed for event {typeof(TEvent).Name}");

    // Track unique perspective names (avoid double-counting)
    if (!_completedPerspectives.Contains(perspectiveName)) {
      _completedPerspectives.Add(perspectiveName);
      var currentCount = Interlocked.Increment(ref _completionCount);

      Console.WriteLine($"[CountingReceptor] Unique perspective count: {currentCount}/{_expectedCount}");

      // Signal completion when all expected unique perspectives have processed
      if (currentCount >= _expectedCount) {
        Console.WriteLine($"[CountingReceptor] ALL {_expectedCount} perspectives completed! Signaling completion.");
        _completionSource.TrySetResult(true);
      }
    } else {
      Console.WriteLine($"[CountingReceptor] Perspective '{perspectiveName}' already counted (duplicate invocation)");
    }

    return ValueTask.CompletedTask;
  }
}
