using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Waiter that registers lifecycle receptors BEFORE sending commands to avoid race conditions.
/// Receptors are registered immediately upon creation, then you can send commands and wait for completion.
/// </summary>
/// <typeparam name="TEvent">The event type to wait for</typeparam>
public sealed class PerspectiveCompletionWaiter<TEvent> : IDisposable
  where TEvent : IEvent {

  private readonly ILifecycleReceptorRegistry _inventoryRegistry;
  private readonly ILifecycleReceptorRegistry _bffRegistry;
  private readonly CountingPerspectiveReceptor<TEvent> _inventoryReceptor;
  private readonly CountingPerspectiveReceptor<TEvent> _bffReceptor;
  private readonly TaskCompletionSource<bool> _inventoryCompletionSource;
  private readonly TaskCompletionSource<bool> _bffCompletionSource;
  private readonly int _inventoryPerspectives;
  private readonly int _bffPerspectives;
  private readonly ILogger<PerspectiveCompletionWaiter<TEvent>>? _logger;

  public PerspectiveCompletionWaiter(
    ILifecycleReceptorRegistry inventoryRegistry,
    ILifecycleReceptorRegistry bffRegistry,
    int inventoryPerspectives,
    int bffPerspectives,
    ILogger<PerspectiveCompletionWaiter<TEvent>>? logger = null) {

    _inventoryRegistry = inventoryRegistry ?? throw new ArgumentNullException(nameof(inventoryRegistry));
    _bffRegistry = bffRegistry ?? throw new ArgumentNullException(nameof(bffRegistry));
    _inventoryPerspectives = inventoryPerspectives;
    _bffPerspectives = bffPerspectives;
    _logger = logger;

    var totalPerspectives = inventoryPerspectives + bffPerspectives;
    Console.WriteLine($"[PerspectiveWaiter] Creating waiter for {typeof(TEvent).Name} (Inventory={inventoryPerspectives}, BFF={bffPerspectives}, Total={totalPerspectives})");

    _inventoryCompletionSource = new TaskCompletionSource<bool>();
    var inventoryCompletedPerspectives = new System.Collections.Concurrent.ConcurrentBag<string>();

    _bffCompletionSource = new TaskCompletionSource<bool>();
    var bffCompletedPerspectives = new System.Collections.Concurrent.ConcurrentBag<string>();

    // Create separate receptor instances for each host
    _inventoryReceptor = new CountingPerspectiveReceptor<TEvent>(
      _inventoryCompletionSource,
      inventoryCompletedPerspectives,
      inventoryPerspectives
    );

    _bffReceptor = new CountingPerspectiveReceptor<TEvent>(
      _bffCompletionSource,
      bffCompletedPerspectives,
      bffPerspectives
    );

    // CRITICAL: If expectedCount is 0, signal completion immediately
    if (inventoryPerspectives == 0) {
      Console.WriteLine($"[PerspectiveWaiter] Inventory expects 0 perspectives, signaling immediate completion");
      _inventoryCompletionSource.TrySetResult(true);
    }
    if (bffPerspectives == 0) {
      Console.WriteLine($"[PerspectiveWaiter] BFF expects 0 perspectives, signaling immediate completion");
      _bffCompletionSource.TrySetResult(true);
    }

    // CRITICAL: Register receptors NOW (before command is sent)
    Console.WriteLine($"[PerspectiveWaiter] Registering receptors for {typeof(TEvent).Name}");
    _inventoryRegistry.Register<TEvent>(_inventoryReceptor, LifecycleStage.PostPerspectiveInline);
    _bffRegistry.Register<TEvent>(_bffReceptor, LifecycleStage.PostPerspectiveInline);
    Console.WriteLine($"[PerspectiveWaiter] Receptors registered! Ready to send command.");
  }

  /// <summary>
  /// Wait for all expected perspectives to complete processing.
  /// </summary>
  /// <param name="timeoutMilliseconds">Timeout in milliseconds (default: 45000ms for Service Bus emulator latency)</param>
  public async Task WaitAsync(int timeoutMilliseconds = 45000) {
    var totalPerspectives = _inventoryPerspectives + _bffPerspectives;
    Console.WriteLine($"[PerspectiveWaiter] Waiting for {typeof(TEvent).Name} processing (Inventory={_inventoryPerspectives}, BFF={_bffPerspectives}, Total={totalPerspectives}, timeout={timeoutMilliseconds}ms)");

    try {
      // Wait for BOTH hosts to complete their perspectives
      await Task.WhenAll(
        _inventoryCompletionSource.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds)),
        _bffCompletionSource.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds))
      );
      Console.WriteLine($"[PerspectiveWaiter] All {totalPerspectives} perspectives completed for {typeof(TEvent).Name}!");
    } catch (TimeoutException) {
      Console.WriteLine($"[PerspectiveWaiter] TIMEOUT waiting for {typeof(TEvent).Name} after {timeoutMilliseconds}ms");
      Console.WriteLine($"[PerspectiveWaiter] Inventory completed: {_inventoryCompletionSource.Task.IsCompleted}");
      Console.WriteLine($"[PerspectiveWaiter] BFF completed: {_bffCompletionSource.Task.IsCompleted}");
      throw;
    }
  }

  /// <summary>
  /// Unregister receptors from both hosts.
  /// </summary>
  public void Dispose() {
    Console.WriteLine($"[PerspectiveWaiter] Disposing waiter for {typeof(TEvent).Name}");
    _inventoryRegistry.Unregister<TEvent>(_inventoryReceptor, LifecycleStage.PostPerspectiveInline);
    _bffRegistry.Unregister<TEvent>(_bffReceptor, LifecycleStage.PostPerspectiveInline);
  }
}
