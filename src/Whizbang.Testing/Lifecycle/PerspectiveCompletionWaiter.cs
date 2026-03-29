using System.Collections.Concurrent;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Testing.Lifecycle;

/// <summary>
/// Waiter that registers lifecycle receptors BEFORE sending commands to avoid race conditions.
/// Receptors are registered immediately upon creation, then you can send commands and wait for completion.
/// </summary>
/// <typeparam name="TEvent">The event type to wait for.</typeparam>
/// <remarks>
/// This class is designed for integration tests that need to wait for perspectives to complete
/// across multiple hosts before making assertions.
///
/// Usage:
/// <code>
/// using var waiter = new PerspectiveCompletionWaiter&lt;ProductCreatedEvent&gt;(
///   inventoryRegistry, bffRegistry,
///   inventoryPerspectives: 2, bffPerspectives: 1);
///
/// // Send command that triggers event
/// await dispatcher.SendAsync(new CreateProductCommand());
///
/// // Wait for perspectives to process
/// await waiter.WaitAsync();
/// </code>
/// </remarks>
public sealed class PerspectiveCompletionWaiter<TEvent> : IDisposable
  where TEvent : IEvent {

  private readonly IReceptorRegistry _inventoryRegistry;
  private readonly IReceptorRegistry _bffRegistry;
  private readonly CountingPerspectiveReceptor<TEvent> _inventoryReceptor;
  private readonly CountingPerspectiveReceptor<TEvent> _bffReceptor;
  private readonly TaskCompletionSource<bool> _inventoryCompletionSource;
  private readonly TaskCompletionSource<bool> _bffCompletionSource;
  private readonly int _inventoryPerspectives;
  private readonly int _bffPerspectives;

  /// <summary>
  /// Creates a new perspective completion waiter for two hosts (inventory and BFF pattern).
  /// Receptors are registered immediately - this must be done BEFORE sending commands.
  /// </summary>
  /// <param name="inventoryRegistry">Lifecycle registry for the inventory/backend host.</param>
  /// <param name="bffRegistry">Lifecycle registry for the BFF/frontend host.</param>
  /// <param name="inventoryPerspectives">Number of perspectives expected on inventory host.</param>
  /// <param name="bffPerspectives">Number of perspectives expected on BFF host.</param>
  public PerspectiveCompletionWaiter(
    IReceptorRegistry inventoryRegistry,
    IReceptorRegistry bffRegistry,
    int inventoryPerspectives,
    int bffPerspectives) {

    _inventoryRegistry = inventoryRegistry ?? throw new ArgumentNullException(nameof(inventoryRegistry));
    _bffRegistry = bffRegistry ?? throw new ArgumentNullException(nameof(bffRegistry));
    _inventoryPerspectives = inventoryPerspectives;
    _bffPerspectives = bffPerspectives;

    var totalPerspectives = inventoryPerspectives + bffPerspectives;
    Console.WriteLine($"[PerspectiveWaiter] Creating waiter for {typeof(TEvent).Name} (Inventory={inventoryPerspectives}, BFF={bffPerspectives}, Total={totalPerspectives})");

    // CRITICAL: Use RunContinuationsAsynchronously to prevent deadlocks
    _inventoryCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var inventoryCompletedPerspectives = new ConcurrentDictionary<string, byte>();

    // CRITICAL: Use RunContinuationsAsynchronously to prevent deadlocks
    _bffCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var bffCompletedPerspectives = new ConcurrentDictionary<string, byte>();

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
      Console.WriteLine("[PerspectiveWaiter] Inventory expects 0 perspectives, signaling immediate completion");
      _inventoryCompletionSource.TrySetResult(true);
    }
    if (bffPerspectives == 0) {
      Console.WriteLine("[PerspectiveWaiter] BFF expects 0 perspectives, signaling immediate completion");
      _bffCompletionSource.TrySetResult(true);
    }

    // CRITICAL: Register receptors NOW (before command is sent)
    Console.WriteLine($"[PerspectiveWaiter] Registering receptors for {typeof(TEvent).Name}");
    _inventoryRegistry.Register<TEvent>(_inventoryReceptor, LifecycleStage.PostPerspectiveInline);
    _bffRegistry.Register<TEvent>(_bffReceptor, LifecycleStage.PostPerspectiveInline);
    Console.WriteLine("[PerspectiveWaiter] Receptors registered! Ready to send command.");
  }

  /// <summary>
  /// Wait for all expected perspectives to complete processing.
  /// </summary>
  /// <param name="timeoutMilliseconds">Timeout in milliseconds (default: 45000ms for transport latency).</param>
  /// <exception cref="TimeoutException">Thrown if perspectives don't complete within timeout.</exception>
  public async Task WaitAsync(int timeoutMilliseconds = 45000) {
    var effectiveTimeout = TestTimeouts.Scale(timeoutMilliseconds);
    var totalPerspectives = _inventoryPerspectives + _bffPerspectives;
    Console.WriteLine($"[PerspectiveWaiter] Waiting for {typeof(TEvent).Name} processing (Inventory={_inventoryPerspectives}, BFF={_bffPerspectives}, Total={totalPerspectives}, timeout={effectiveTimeout}ms{(TestTimeouts.Multiplier > 1 ? $" [x{TestTimeouts.Multiplier}]" : "")})");

    try {
      // Wait for BOTH hosts to complete their perspectives
      await Task.WhenAll(
        _inventoryCompletionSource.Task.WaitAsync(TimeSpan.FromMilliseconds(effectiveTimeout)),
        _bffCompletionSource.Task.WaitAsync(TimeSpan.FromMilliseconds(effectiveTimeout))
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
