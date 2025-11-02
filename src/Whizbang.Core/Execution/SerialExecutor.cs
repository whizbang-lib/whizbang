using System.Threading.Channels;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Execution;

/// <summary>
/// Executes handlers serially in strict FIFO order.
/// All messages are processed one at a time, preserving exact ordering.
/// </summary>
public class SerialExecutor : IExecutionStrategy {
  private enum State { NotStarted, Running, Stopped }

  private readonly Channel<WorkItem> _channel;
  private State _state = State.NotStarted;
  private Task? _workerTask;
  private CancellationTokenSource? _workerCts;
  private readonly object _stateLock = new();
  private bool _channelCompleted = false;

  public SerialExecutor() {
    // Unbounded channel for pending work
    _channel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions {
      SingleReader = true, // Only one worker processes messages
      SingleWriter = false // Multiple threads can enqueue
    });
  }

  public string Name => "Serial";

  public async Task<TResult> ExecuteAsync<TResult>(
    IMessageEnvelope envelope,
    Func<IMessageEnvelope, PolicyContext, Task<TResult>> handler,
    PolicyContext context,
    CancellationToken ct = default
  ) {
    lock (_stateLock) {
      if (_state != State.Running) {
        throw new InvalidOperationException("SerialExecutor is not running. Call StartAsync first.");
      }
    }

    var tcs = new TaskCompletionSource<TResult>();
    var workItem = new WorkItem(
      envelope,
      context,
      async () => {
        try {
          var result = await handler(envelope, context);
          tcs.SetResult(result);
        } catch (Exception ex) {
          tcs.SetException(ex);
        }
      },
      ct
    );

    await _channel.Writer.WriteAsync(workItem, ct);
    return await tcs.Task;
  }

  public Task StartAsync(CancellationToken ct = default) {
    lock (_stateLock) {
      if (_state == State.Running) {
        return Task.CompletedTask; // Idempotent
      }

      if (_state == State.Stopped) {
        throw new InvalidOperationException("Cannot restart a stopped SerialExecutor");
      }

      _state = State.Running;
      _workerCts = new CancellationTokenSource();
      _workerTask = Task.Run(() => ProcessWorkItemsAsync(_workerCts.Token), _workerCts.Token);
    }

    return Task.CompletedTask;
  }

  public async Task StopAsync(CancellationToken ct = default) {
    lock (_stateLock) {
      if (_state == State.Stopped) {
        return; // Already stopped
      }

      if (_state == State.NotStarted) {
        _state = State.Stopped;
        return;
      }

      _state = State.Stopped;

      if (!_channelCompleted) {
        _channel.Writer.Complete();
        _channelCompleted = true;
      }
    }

    _workerCts?.Cancel();

    if (_workerTask != null) {
      try {
        await _workerTask;
      } catch (OperationCanceledException) {
        // Expected when cancelling worker
      }
    }
  }

  public async Task DrainAsync(CancellationToken ct = default) {
    lock (_stateLock) {
      if (_state != State.Running) {
        return; // Nothing to drain
      }

      // Complete the channel writer to signal no more work
      if (!_channelCompleted) {
        _channel.Writer.Complete();
        _channelCompleted = true;
      }
    }

    // Wait for worker to finish processing all items
    if (_workerTask != null) {
      try {
        await _workerTask;
      } catch (OperationCanceledException) {
        // Expected if worker was cancelled
      }
    }
  }

  private async Task ProcessWorkItemsAsync(CancellationToken ct) {
    await foreach (var workItem in _channel.Reader.ReadAllAsync(ct)) {
      if (workItem.CancellationToken.IsCancellationRequested) {
        continue; // Skip cancelled work
      }

      try {
        await workItem.ExecuteAsync();
      } catch {
        // Exceptions are captured in TaskCompletionSource
      }
    }
  }

  private record WorkItem(
    IMessageEnvelope Envelope,
    PolicyContext Context,
    Func<Task> ExecuteAsync,
    CancellationToken CancellationToken
  );
}
