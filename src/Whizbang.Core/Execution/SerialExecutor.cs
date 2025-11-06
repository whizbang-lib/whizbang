using System.Threading.Channels;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.Pooling;

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

  /// <summary>
  /// Creates a new SerialExecutor with an unbounded channel.
  /// </summary>
  public SerialExecutor() : this(channelCapacity: null) {
  }

  /// <summary>
  /// Creates a new SerialExecutor with a bounded or unbounded channel.
  /// </summary>
  /// <param name="channelCapacity">
  /// The maximum number of pending messages. If null, uses an unbounded channel.
  /// Bounded channels can reduce memory overhead but may block senders when full.
  /// </param>
  public SerialExecutor(int? channelCapacity) {
    if (channelCapacity.HasValue) {
      if (channelCapacity.Value <= 0) {
        throw new ArgumentOutOfRangeException(nameof(channelCapacity), "Channel capacity must be greater than zero");
      }

      // Bounded channel with pre-allocated capacity (lower overhead)
      _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(channelCapacity.Value) {
        SingleReader = true,  // Only one worker processes messages
        SingleWriter = false, // Multiple threads can enqueue
        FullMode = BoundedChannelFullMode.Wait // Block writers when full
      });
    } else {
      // Unbounded channel for pending work (safer default)
      _channel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions {
        SingleReader = true,  // Only one worker processes messages
        SingleWriter = false  // Multiple threads can enqueue
      });
    }
  }

  public string Name => "Serial";

  public async ValueTask<TResult> ExecuteAsync<TResult>(
    IMessageEnvelope envelope,
    Func<IMessageEnvelope, PolicyContext, ValueTask<TResult>> handler,
    PolicyContext context,
    CancellationToken ct = default
  ) {
    lock (_stateLock) {
      if (_state != State.Running) {
        throw new InvalidOperationException("SerialExecutor is not running. Call StartAsync first.");
      }
    }

    // Create value task source - cannot be pooled due to lifetime spanning caller and worker contexts
    // The source must remain valid until the caller calls GetResult on the ValueTask
    var source = new PooledValueTaskSource<TResult>();
    var token = source.Token;

    // Rent pooled execution state to eliminate lambda closure allocation
    var state = ExecutionStatePool<TResult>.Rent();
    state.Initialize(envelope, context, handler, source);

    var workItem = new WorkItem(
      envelope: envelope,
      context: context,
      executeAsync: ExecuteWithPooledStateAsync<TResult>,
      state: state,
      cancellationToken: ct
    );

    await _channel.Writer.WriteAsync(workItem, ct);
    return await new ValueTask<TResult>(source, token);
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
        await workItem.ExecuteAsync(workItem.State);
      } catch {
        // Exceptions are captured in PooledValueTaskSource
      }
    }
  }

  /// <summary>
  /// Static delegate method that executes handler with pooled state.
  /// Eliminates lambda closure allocations.
  /// </summary>
  private static async ValueTask ExecuteWithPooledStateAsync<TResult>(object? stateObj) {
    var state = (ExecutionState<TResult>)stateObj!;
    try {
      var result = await state.Handler(state.Envelope, state.Context);
      state.Source.SetResult(result);
    } catch (Exception ex) {
      state.Source.SetException(ex);
    } finally {
      // Return state to pool after execution
      // Note: Cannot pool PooledValueTaskSource - it must remain valid until GetResult is called
      state.Reset();
      ExecutionStatePool<TResult>.Return(state);
    }
  }

  private readonly struct WorkItem {
    public readonly IMessageEnvelope Envelope;
    public readonly PolicyContext Context;
    public readonly Func<object?, ValueTask> ExecuteAsync;
    public readonly object? State;
    public readonly CancellationToken CancellationToken;

    public WorkItem(
      IMessageEnvelope envelope,
      PolicyContext context,
      Func<object?, ValueTask> executeAsync,
      object? state,
      CancellationToken cancellationToken
    ) {
      Envelope = envelope;
      Context = context;
      ExecuteAsync = executeAsync;
      State = state;
      CancellationToken = cancellationToken;
    }
  }
}
