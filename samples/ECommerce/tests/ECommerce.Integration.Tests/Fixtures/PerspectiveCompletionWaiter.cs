using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
  private readonly ILogger _logger;

  public PerspectiveCompletionWaiter(
    ILifecycleReceptorRegistry inventoryRegistry,
    ILifecycleReceptorRegistry bffRegistry,
    int inventoryPerspectives,
    int bffPerspectives,
    ILogger? logger = null) {

    _inventoryRegistry = inventoryRegistry ?? throw new ArgumentNullException(nameof(inventoryRegistry));
    _bffRegistry = bffRegistry ?? throw new ArgumentNullException(nameof(bffRegistry));
    _inventoryPerspectives = inventoryPerspectives;
    _bffPerspectives = bffPerspectives;
    _logger = logger ?? NullLogger.Instance;

    var totalPerspectives = inventoryPerspectives + bffPerspectives;
    _logger.LogDebug("[PerspectiveWaiter] Creating waiter for {EventType} (Inventory={InventoryPerspectives}, BFF={BffPerspectives}, Total={TotalPerspectives})",
      typeof(TEvent).Name, inventoryPerspectives, bffPerspectives, totalPerspectives);

    _inventoryCompletionSource = new TaskCompletionSource<bool>();
    var inventoryCompletedPerspectives = new System.Collections.Concurrent.ConcurrentBag<string>();

    _bffCompletionSource = new TaskCompletionSource<bool>();
    var bffCompletedPerspectives = new System.Collections.Concurrent.ConcurrentBag<string>();

    // Create separate receptor instances for each host
    _inventoryReceptor = new CountingPerspectiveReceptor<TEvent>(
      _inventoryCompletionSource,
      inventoryCompletedPerspectives,
      inventoryPerspectives,
      logger
    );

    _bffReceptor = new CountingPerspectiveReceptor<TEvent>(
      _bffCompletionSource,
      bffCompletedPerspectives,
      bffPerspectives,
      logger
    );

    // CRITICAL: Register receptors NOW (before command is sent)
    _logger.LogDebug("[PerspectiveWaiter] Registering receptors for {EventType}", typeof(TEvent).Name);
    _inventoryRegistry.Register<TEvent>(_inventoryReceptor, LifecycleStage.PostPerspectiveInline);
    _bffRegistry.Register<TEvent>(_bffReceptor, LifecycleStage.PostPerspectiveInline);
    _logger.LogDebug("[PerspectiveWaiter] Receptors registered! Ready to send command.");
  }

  /// <summary>
  /// Wait for all expected perspectives to complete processing.
  /// </summary>
  /// <param name="timeoutMilliseconds">Timeout in milliseconds (default: 45000ms for Service Bus emulator latency)</param>
  public async Task WaitAsync(int timeoutMilliseconds = 45000) {
    var totalPerspectives = _inventoryPerspectives + _bffPerspectives;
    _logger.LogDebug("[PerspectiveWaiter] Waiting for {EventType} processing (Inventory={InventoryPerspectives}, BFF={BffPerspectives}, Total={TotalPerspectives}, timeout={TimeoutMs}ms)",
      typeof(TEvent).Name, _inventoryPerspectives, _bffPerspectives, totalPerspectives, timeoutMilliseconds);

    try {
      // Wait for BOTH hosts to complete their perspectives
      await Task.WhenAll(
        _inventoryCompletionSource.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds)),
        _bffCompletionSource.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds))
      );
      _logger.LogInformation("[PerspectiveWaiter] All {TotalPerspectives} perspectives completed for {EventType}!",
        totalPerspectives, typeof(TEvent).Name);
    } catch (TimeoutException) {
      _logger.LogError("[PerspectiveWaiter] TIMEOUT waiting for {EventType} after {TimeoutMs}ms. Inventory completed: {InventoryCompleted}, BFF completed: {BffCompleted}",
        typeof(TEvent).Name, timeoutMilliseconds, _inventoryCompletionSource.Task.IsCompleted, _bffCompletionSource.Task.IsCompleted);
      throw;
    }
  }

  /// <summary>
  /// Unregister receptors from both hosts.
  /// </summary>
  public void Dispose() {
    _logger.LogDebug("[PerspectiveWaiter] Disposing waiter for {EventType}", typeof(TEvent).Name);
    _inventoryRegistry.Unregister<TEvent>(_inventoryReceptor, LifecycleStage.PostPerspectiveInline);
    _bffRegistry.Unregister<TEvent>(_bffReceptor, LifecycleStage.PostPerspectiveInline);
    _logger.LogDebug("[PerspectiveWaiter] Receptors unregistered for {EventType}", typeof(TEvent).Name);
  }
}
