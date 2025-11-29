using System.Collections.Concurrent;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Execution;

/// <summary>
/// Executes handlers concurrently with no ordering guarantees.
/// Supports configurable concurrency limits via SemaphoreSlim.
/// </summary>
public class ParallelExecutor : IExecutionStrategy {
  private enum State { NotStarted, Running, Stopped }

  private readonly SemaphoreSlim _semaphore;
  private readonly int _maxConcurrency;
  private State _state = State.NotStarted;
  private readonly Lock _stateLock = new();
  private readonly ConcurrentBag<Task> _runningTasks = [];

  public ParallelExecutor(int maxConcurrency = 10) {
    if (maxConcurrency <= 0) {
      throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "Max concurrency must be greater than zero");
    }

    _maxConcurrency = maxConcurrency;
    _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
  }

  public string Name => $"Parallel(max:{_maxConcurrency})";

  public ValueTask<TResult> ExecuteAsync<TResult>(
    IMessageEnvelope envelope,
    Func<IMessageEnvelope, PolicyContext, ValueTask<TResult>> handler,
    PolicyContext context,
    CancellationToken ct = default
  ) {
    lock (_stateLock) {
      if (_state != State.Running) {
        throw new InvalidOperationException("ParallelExecutor is not running. Call StartAsync first.");
      }
    }

    // Fast path: try synchronous acquire (zero allocation if successful)
    if (_semaphore.Wait(0, ct)) {
      try {
        var result = handler(envelope, context);

        // If handler completed synchronously, return directly (zero allocation)
        if (result.IsCompleted) {
          _semaphore.Release();
          return result;
        }

        // Handler is async, await it
        return AwaitAndReleaseAsync(result, _semaphore);
      } catch {
        _semaphore.Release();
        throw;
      }
    }

    // Slow path: async wait (allocates if we need to wait)
    return SlowPathAsync(envelope, handler, context, ct);
  }

  private static async ValueTask<TResult> AwaitAndReleaseAsync<TResult>(
    ValueTask<TResult> task,
    SemaphoreSlim semaphore
  ) {
    try {
      return await task;
    } finally {
      semaphore.Release();
    }
  }

  private async ValueTask<TResult> SlowPathAsync<TResult>(
    IMessageEnvelope envelope,
    Func<IMessageEnvelope, PolicyContext, ValueTask<TResult>> handler,
    PolicyContext context,
    CancellationToken ct
  ) {
    await _semaphore.WaitAsync(ct);
    try {
      return await handler(envelope, context);
    } finally {
      _semaphore.Release();
    }
  }

  public Task StartAsync(CancellationToken ct = default) {
    lock (_stateLock) {
      if (_state == State.Running) {
        return Task.CompletedTask; // Idempotent
      }

      if (_state == State.Stopped) {
        throw new InvalidOperationException("Cannot restart a stopped ParallelExecutor");
      }

      _state = State.Running;
    }

    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken ct = default) {
    lock (_stateLock) {
      if (_state == State.Stopped) {
        return Task.CompletedTask; // Already stopped
      }

      if (_state == State.NotStarted) {
        _state = State.Stopped;
        return Task.CompletedTask;
      }

      _state = State.Stopped;
    }

    return Task.CompletedTask;
  }

  public async Task DrainAsync(CancellationToken ct = default) {
    lock (_stateLock) {
      if (_state != State.Running) {
        return; // Nothing to drain
      }
    }

    // Wait for all semaphore slots to be available (all work complete)
    for (int i = 0; i < _maxConcurrency; i++) {
      await _semaphore.WaitAsync(ct);
    }

    // Release all slots back
    _semaphore.Release(_maxConcurrency);
  }
}
