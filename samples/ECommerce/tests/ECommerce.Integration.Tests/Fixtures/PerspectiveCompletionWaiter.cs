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
  private readonly int _expectedPerspectiveCount;

  public PerspectiveCompletionWaiter(
    ILifecycleReceptorRegistry inventoryRegistry,
    ILifecycleReceptorRegistry bffRegistry,
    int expectedPerspectiveCount) {

    _inventoryRegistry = inventoryRegistry ?? throw new ArgumentNullException(nameof(inventoryRegistry));
    _bffRegistry = bffRegistry ?? throw new ArgumentNullException(nameof(bffRegistry));
    _expectedPerspectiveCount = expectedPerspectiveCount;

    Console.WriteLine($"[PerspectiveWaiter] Creating waiter for {typeof(TEvent).Name} (expecting {expectedPerspectiveCount} total perspectives)");

    // IMPORTANT: Both InventoryHost and BFFHost process perspectives
    // Create SEPARATE receptors for each host (they have perspectives with the same names!)
    var perspectivesPerHost = expectedPerspectiveCount / 2;

    _inventoryCompletionSource = new TaskCompletionSource<bool>();
    var inventoryCompletedPerspectives = new System.Collections.Concurrent.ConcurrentBag<string>();

    _bffCompletionSource = new TaskCompletionSource<bool>();
    var bffCompletedPerspectives = new System.Collections.Concurrent.ConcurrentBag<string>();

    // Create separate receptor instances for each host
    _inventoryReceptor = new CountingPerspectiveReceptor<TEvent>(
      _inventoryCompletionSource,
      inventoryCompletedPerspectives,
      perspectivesPerHost
    );

    _bffReceptor = new CountingPerspectiveReceptor<TEvent>(
      _bffCompletionSource,
      bffCompletedPerspectives,
      perspectivesPerHost
    );

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
    Console.WriteLine($"[PerspectiveWaiter] Waiting for {typeof(TEvent).Name} processing (timeout={timeoutMilliseconds}ms)");

    try {
      // Wait for BOTH hosts to complete their perspectives
      await Task.WhenAll(
        _inventoryCompletionSource.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds)),
        _bffCompletionSource.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds))
      );
      Console.WriteLine($"[PerspectiveWaiter] All {_expectedPerspectiveCount} perspectives completed for {typeof(TEvent).Name}!");
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
