using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Testing.Lifecycle;

/// <summary>
/// Awaiter for perspective completion across multiple hosts.
/// Useful for integration tests with separate Inventory and BFF hosts that both process the same events.
/// </summary>
/// <typeparam name="TEvent">The event type being processed by perspectives.</typeparam>
public sealed class MultiHostPerspectiveAwaiter<TEvent> : IDisposable
  where TEvent : IEvent {

  private readonly List<(IHost Host, ILifecycleReceptorRegistry Registry, CountingReceptor Receptor, TaskCompletionSource<bool> Tcs)> _hostRegistrations = [];
  private bool _disposed;

  /// <summary>
  /// Creates a new multi-host perspective awaiter.
  /// </summary>
  /// <param name="hostConfigs">Configuration for each host specifying expected perspective count.</param>
  public MultiHostPerspectiveAwaiter(params (IHost Host, int ExpectedPerspectives)[] hostConfigs) {
    ArgumentNullException.ThrowIfNull(hostConfigs);

    foreach (var (host, expectedPerspectives) in hostConfigs) {
      if (expectedPerspectives <= 0) {
        continue;  // Skip hosts with no expected perspectives
      }

      var registry = host.Services.GetRequiredService<ILifecycleReceptorRegistry>();
      // CRITICAL: Use RunContinuationsAsynchronously to prevent deadlocks
      var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      var receptor = new CountingReceptor(tcs, expectedPerspectives);

      registry.Register<TEvent>(receptor, LifecycleStage.PostPerspectiveInline);
      _hostRegistrations.Add((host, registry, receptor, tcs));
    }
  }

  /// <summary>
  /// Waits for all perspectives across all hosts to complete.
  /// </summary>
  /// <param name="timeout">Maximum time to wait.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <exception cref="TimeoutException">Thrown if not all perspectives complete within timeout.</exception>
  public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) {
    if (_hostRegistrations.Count == 0) {
      return;  // Nothing to wait for
    }

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    cts.CancelAfter(timeout);

    try {
      var tasks = _hostRegistrations.Select(r => r.Tcs.Task);
      await Task.WhenAll(tasks).WaitAsync(cts.Token);
    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
      var status = string.Join(", ", _hostRegistrations.Select(r =>
        $"{r.Host.Services.GetType().Name}: {r.Receptor.Count}/{r.Receptor.Expected}"));
      throw new TimeoutException($"Not all perspectives completed within {timeout}. Status: [{status}]");
    }
  }

  /// <summary>
  /// Waits for all perspectives across all hosts to complete with default timeout.
  /// </summary>
  /// <param name="timeoutMilliseconds">Maximum time to wait in milliseconds.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public Task WaitAsync(int timeoutMilliseconds = 15000, CancellationToken cancellationToken = default) {
    return WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds), cancellationToken);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (_disposed) {
      return;
    }

    foreach (var (_, registry, receptor, _) in _hostRegistrations) {
      registry.Unregister<TEvent>(receptor, LifecycleStage.PostPerspectiveInline);
    }

    _hostRegistrations.Clear();
    _disposed = true;
  }

  /// <summary>
  /// Internal receptor that counts completions and signals when expected count is reached.
  /// </summary>
  private sealed class CountingReceptor : IReceptor<TEvent>, IAcceptsLifecycleContext {
    private readonly TaskCompletionSource<bool> _tcs;
    private readonly int _expected;
    private readonly ConcurrentDictionary<string, byte> _completedPerspectives = new();
    private static readonly AsyncLocal<ILifecycleContext?> _asyncLocalContext = new();

    public int Expected => _expected;
    public int Count => _completedPerspectives.Count;

    public CountingReceptor(TaskCompletionSource<bool> tcs, int expected) {
      _tcs = tcs;
      _expected = expected;
    }

    public ValueTask HandleAsync(TEvent message, CancellationToken cancellationToken = default) {
      var context = _asyncLocalContext.Value;
      var perspectiveKey = context?.PerspectiveType?.FullName ?? $"unknown-{Guid.NewGuid()}";

      // Track unique perspectives (deduplicate by perspective type)
      if (_completedPerspectives.TryAdd(perspectiveKey, 0)) {
        if (_completedPerspectives.Count >= _expected) {
          _tcs.TrySetResult(true);
        }
      }

      return ValueTask.CompletedTask;
    }

    public void SetLifecycleContext(ILifecycleContext context) {
      _asyncLocalContext.Value = context;
    }
  }
}

/// <summary>
/// Factory for creating multi-host perspective awaiters.
/// </summary>
public static class PerspectiveAwaiter {
  /// <summary>
  /// Creates an awaiter for perspective completion across multiple hosts.
  /// </summary>
  /// <typeparam name="TEvent">The event type being processed.</typeparam>
  /// <param name="hostConfigs">Configuration for each host.</param>
  /// <returns>A disposable awaiter.</returns>
  public static MultiHostPerspectiveAwaiter<TEvent> ForHosts<TEvent>(
    params (IHost Host, int ExpectedPerspectives)[] hostConfigs)
    where TEvent : IEvent {
    return new MultiHostPerspectiveAwaiter<TEvent>(hostConfigs);
  }

  /// <summary>
  /// Creates an awaiter for a typical two-host scenario (e.g., Inventory + BFF).
  /// </summary>
  /// <typeparam name="TEvent">The event type being processed.</typeparam>
  /// <param name="inventoryHost">The inventory/write-side host.</param>
  /// <param name="inventoryPerspectives">Expected perspective count for inventory host.</param>
  /// <param name="bffHost">The BFF/read-side host.</param>
  /// <param name="bffPerspectives">Expected perspective count for BFF host.</param>
  /// <returns>A disposable awaiter.</returns>
  public static MultiHostPerspectiveAwaiter<TEvent> ForInventoryAndBff<TEvent>(
    IHost inventoryHost,
    int inventoryPerspectives,
    IHost bffHost,
    int bffPerspectives)
    where TEvent : IEvent {
    return new MultiHostPerspectiveAwaiter<TEvent>(
      (inventoryHost, inventoryPerspectives),
      (bffHost, bffPerspectives)
    );
  }
}
