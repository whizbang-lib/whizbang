using System.Diagnostics;
using System.Threading.Channels;
using Whizbang.Core.Observability;
using Whizbang.Core.Policies;
using Whizbang.Core.Pooling;

namespace Whizbang.Core.Execution;

/// <summary>
/// Executes handlers serially in strict FIFO order.
/// All messages are processed one at a time, preserving exact ordering.
/// </summary>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:Constructor_Default_CreatesUnboundedExecutorAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:Constructor_WithValidBoundedCapacity_CreatesExecutorAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:Constructor_WithInvalidCapacity_ThrowsArgumentOutOfRangeExceptionAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:ExecuteAsync_WhenNotRunning_ThrowsInvalidOperationExceptionAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:StateTransitions_IdempotentOperations_SucceedAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:StartAsync_AfterStop_ThrowsInvalidOperationExceptionAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:ExecuteAsync_CompletesSuccessfullyAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:ExecuteAsync_WithAsyncHandler_AwaitsCorrectlyAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:ExecuteAsync_ExceptionInHandler_RethrowsAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:DrainAsync_WaitsForAllInFlightWork_CompletesAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:DrainAsync_WhenNotRunning_ReturnsImmediatelyAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:ExecuteAsync_SerialExecution_MaintainsStrictOrderAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:ExecuteAsync_CancellationToken_SkipsCanceledWorkAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:DrainAsync_WithWorkerCancellation_HandlesOperationCanceledExceptionAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorDrainAfterStopTests.cs:DrainAsync_WorkerCanceledWhileTheDrainAwaitsIt_CompletesAndRecordsItAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:ProcessWorkItemsAsync_ExceptionInHandler_CaughtAndRecordedAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:ExecuteAsync_BoundedChannel_HandlesBackpressureAsync</tests>
public class SerialExecutor : IExecutionStrategy, IAsyncDisposable {
  private enum State { NotStarted, Running, Stopped }

  private readonly Channel<WorkItem> _channel;
  private State _state = State.NotStarted;
  private Task? _workerTask;
  private CancellationTokenSource? _workerCts;
  private readonly Lock _stateLock = new();
  private bool _channelCompleted;
  private bool _disposed;

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

  /// <inheritdoc/>
  public string Name => "Serial";

  /// <inheritdoc/>
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
      executeAsync: _executeWithPooledStateAsync<TResult>,
      cancelAsync: _cancelPooledStateAsync<TResult>,
      state: state,
      cancellationToken: ct
    );

    await _channel.Writer.WriteAsync(workItem, ct);
    return await new ValueTask<TResult>(source, token);
  }

  /// <summary>
  /// Enqueues a work item whose execute delegate is supplied by the caller. This is the second
  /// construction site the catch in <see cref="_processWorkItemsAsync"/> describes, and it exists
  /// so that net can be exercised: nothing on the ordinary path can hand the worker a delegate
  /// that faults, and a net nobody has ever seen work is not a net.
  /// </summary>
  /// <param name="executeAsync">What the worker runs for this item.</param>
  /// <param name="itemCancellationToken">
  /// The token the WORK ITEM carries, which is what the worker tests before running it. It does not
  /// cancel the enqueue: a caller that wants to exercise the canceled-while-queued branch has to be
  /// able to enqueue an item whose token is already canceled.
  /// </param>
  internal async Task EnqueueFaultingForTestsAsync(
      Func<object?, ValueTask> executeAsync,
      CancellationToken itemCancellationToken = default) {
    ArgumentNullException.ThrowIfNull(executeAsync);
    var workItem = new WorkItem(
      // Nothing to finish: there is no pooled state and no caller awaiting a source, so an item this
      // seam enqueues with a canceled token is simply not run.
      executeAsync: executeAsync,
      cancelAsync: static (_, _) => ValueTask.CompletedTask,
      state: null,
      cancellationToken: itemCancellationToken
    );
    await _channel.Writer.WriteAsync(workItem, CancellationToken.None);
  }

  /// <inheritdoc/>
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
      _workerTask = Task.Run(() => _processWorkItemsAsync(_workerCts.Token), _workerCts.Token);
    }

    return Task.CompletedTask;
  }

  /// <inheritdoc/>
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

    if (_workerCts != null) {
      await _workerCts.CancelAsync();
    }

    if (_workerTask != null) {
      try {
        await _workerTask;
      } catch (OperationCanceledException) {
        // Expected when cancelling worker
      }
    }
  }

  /// <inheritdoc/>
  public async Task DrainAsync(CancellationToken ct = default) {
    using var activity = WhizbangActivitySource.Execution.StartActivity("SerialExecutor.DrainAsync");

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
        // A stop that lands while this drain is already awaiting the worker. Everything above the
        // await is synchronous, so a caller can have the channel completed and this task suspended
        // here when StopAsync cancels the worker token; the worker's next read observes the canceled
        // token ahead of "done writing" and its task ends canceled. Swallowed for the caller, who
        // asked only to drain, and recorded so an operator can see it happened.
        // SerialExecutorDrainAfterStopTests reaches this deterministically.
        WhizbangActivitySource.RecordDefensiveCancellation(
          activity,
          "Worker canceled during DrainAsync after channel completion"
        );
        // Note: We still swallow the exception but now it's observable via OpenTelemetry
      }
    }
  }

  private async Task _processWorkItemsAsync(CancellationToken ct) {
    using var activity = WhizbangActivitySource.Execution.StartActivity("SerialExecutor.ProcessWorkItems");

    await foreach (var workItem in _channel.Reader.ReadAllAsync(ct)) {
      // Cancellation arriving AFTER the item was queued and before the worker reached it.
      // WriteAsync rejects an already-canceled token, so this is the only way to get here --
      // and it is ordinary, not defensive: a timeout or a shutdown firing while the worker is
      // busy with earlier work. The caller must be finished rather than skipped, because only
      // the execute path completes its value-task source.
      if (workItem.CancellationToken.IsCancellationRequested) {
        WhizbangActivitySource.RecordDefensiveCancellation(
          activity,
          "Work item canceled after queueing but before execution"
        );
        await workItem.CancelAsync(workItem.State, workItem.CancellationToken);
        continue; // Handler is not run; the caller observes OperationCanceledException.
      }

      try {
        await workItem.ExecuteAsync(workItem.State);
      } catch (Exception ex) {
        // The ordinary path cannot get here: the only delegate the public enqueue assigns is
        // _executeWithPooledStateAsync, whose own try, catch and finally cover its whole body, so
        // a faulting handler is recorded on the pooled source and this await completes. What the
        // net is for is the rest of that delegate -- completing an already-completed source,
        // resetting state, returning it to the pool -- any of which throwing would otherwise end
        // the worker loop silently. EnqueueFaultingForTestsAsync is the second construction site
        // that lets a test hand the worker a delegate that faults, so the net is exercised.
        WhizbangActivitySource.RecordDefensiveException(
          activity,
          ex,
          "Unexpected exception escaped work item execution"
        );
        // Note: We still swallow the exception but now it's observable via OpenTelemetry
      }
    }
  }

  /// <summary>
  /// Static delegate method that executes handler with pooled state.
  /// Eliminates lambda closure allocations.
  /// </summary>
  /// <summary>
  /// Finishes a work item the worker is going to skip because its token was canceled while it
  /// sat in the channel.
  /// </summary>
  /// <remarks>
  /// Without this the caller's <c>await</c> never returns. Only the execute path completes the
  /// value-task source, so skipping the item left the source permanently incomplete -- a hang
  /// with no exception and nothing logged -- and leaked the pooled state as well.
  /// </remarks>
  private static ValueTask _cancelPooledStateAsync<TResult>(object? stateObj, CancellationToken ct) {
    var state = (ExecutionState<TResult>)stateObj!;
    try {
      state.Source.SetException(new OperationCanceledException(ct));
    } finally {
      state.Reset();
      ExecutionStatePool<TResult>.Return(state);
    }
    return ValueTask.CompletedTask;
  }

  private static async ValueTask _executeWithPooledStateAsync<TResult>(object? stateObj) {
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

  /// <summary>
  /// Represents a unit of work queued for serial execution.
  /// </summary>
  private readonly struct WorkItem(
    Func<object?, ValueTask> executeAsync,
    Func<object?, CancellationToken, ValueTask> cancelAsync,
    object? state,
    CancellationToken cancellationToken
    ) {
    /// <summary>The delegate that executes the handler with pooled state.</summary>
    public readonly Func<object?, ValueTask> ExecuteAsync = executeAsync;
    /// <summary>
    /// Completes the caller's value-task source as canceled and returns the pooled state.
    /// Typed the same way as <see cref="ExecuteAsync"/> so the worker, which has no TResult,
    /// can still finish a caller it is not going to run.
    /// </summary>
    public readonly Func<object?, CancellationToken, ValueTask> CancelAsync = cancelAsync;
    /// <summary>The pooled execution state containing the handler, envelope, and context.</summary>
    public readonly object? State = state;
    /// <summary>The cancellation token associated with this work item.</summary>
    public readonly CancellationToken CancellationToken = cancellationToken;
  }

  /// <summary>Stops the executor and disposes the worker cancellation token source.</summary>
  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }

    // Stop the executor if running
    await StopAsync(CancellationToken.None);

    // Dispose the cancellation token source
    _workerCts?.Dispose();

    _disposed = true;
    GC.SuppressFinalize(this);
  }
}
