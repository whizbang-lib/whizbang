using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
  private readonly ConcurrentBag<string> _completedPerspectives;
  private readonly int _expectedCount;
  private readonly ILogger _logger;
  private int _completionCount = 0;
  private ILifecycleContext? _context;

  public CountingPerspectiveReceptor(
    TaskCompletionSource<bool> completionSource,
    ConcurrentBag<string> completedPerspectives,
    int expectedCount,
    ILogger? logger = null) {

    _completionSource = completionSource ?? throw new ArgumentNullException(nameof(completionSource));
    _completedPerspectives = completedPerspectives ?? throw new ArgumentNullException(nameof(completedPerspectives));
    _expectedCount = expectedCount;
    _logger = logger ?? NullLogger.Instance;

    _logger.LogDebug("[CountingReceptor] Created receptor expecting {ExpectedCount} perspective completions for {EventType}",
      expectedCount, typeof(TEvent).Name);
  }

  /// <inheritdoc/>
  public void SetLifecycleContext(ILifecycleContext context) {
    _context = context;
  }

  public ValueTask HandleAsync(TEvent message, CancellationToken cancellationToken = default) {
    // Get perspective name and stream ID from lifecycle context
    var perspectiveName = _context?.PerspectiveType?.Name ?? "Unknown";
    var streamId = _context?.StreamId?.ToString() ?? "Unknown";

    _logger.LogDebug("[CountingReceptor] Perspective '{PerspectiveName}' completed for event {EventType} on stream {StreamId}",
      perspectiveName, typeof(TEvent).Name, streamId);

    // Track (perspectiveName, streamId) pairs to count per-stream invocations
    // This allows counting multiple events for the same perspective (e.g., 2 ProductCreatedEvents → 2 InventoryLevelsPerspective invocations)
    var key = $"{perspectiveName}:{streamId}";
    if (!_completedPerspectives.Contains(key)) {
      _completedPerspectives.Add(key);
      var currentCount = Interlocked.Increment(ref _completionCount);

      _logger.LogDebug("[CountingReceptor] Unique perspective-stream count: {CurrentCount}/{ExpectedCount}",
        currentCount, _expectedCount);

      // Signal completion when all expected perspective-stream pairs have processed
      if (currentCount >= _expectedCount) {
        _logger.LogInformation("[CountingReceptor] ALL {ExpectedCount} perspective-stream pairs completed! Signaling completion for {EventType}",
          _expectedCount, typeof(TEvent).Name);
        _completionSource.TrySetResult(true);
      }
    } else {
      _logger.LogDebug("[CountingReceptor] Perspective-stream '{Key}' already counted (duplicate invocation)",
        key);
    }

    return ValueTask.CompletedTask;
  }
}
