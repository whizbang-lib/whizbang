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
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:ExecuteAsync_CancellationToken_SkipsCancelledWorkAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:DrainAsync_WithWorkerCancellation_HandlesOperationCanceledExceptionAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:ProcessWorkItemsAsync_ExceptionInHandler_CaughtAndRecordedAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/SerialExecutorTests.cs:ExecuteAsync_BoundedChannel_HandlesBackpressureAsync</tests>
public class SerialExecutor : IExecutionStrategy {
  private enum State { NotStarted, Running, Stopped }

  private readonly Channel<WorkItem> _channel;
  private State _state = State.NotStarted;
  private Task? _workerTask;
  private CancellationTokenSource? _workerCts;
  private readonly Lock _stateLock = new();
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
        // DEFENSIVE: Should never happen - channel completes before worker cancellation
        // Kept as safety net for unexpected cancellation timing edge cases
        WhizbangActivitySource.RecordDefensiveCancellation(
          activity,
          "Worker cancelled during DrainAsync after channel completion"
        );
        // Note: We still swallow the exception but now it's observable via OpenTelemetry
      }
    }
  }

  private async Task ProcessWorkItemsAsync(CancellationToken ct) {
    using var activity = WhizbangActivitySource.Execution.StartActivity("SerialExecutor.ProcessWorkItems");

    await foreach (var workItem in _channel.Reader.ReadAllAsync(ct)) {
      // DEFENSIVE: Should never happen - WriteAsync throws before queueing cancelled work
      // Kept as safety net if cancellation happens between WriteAsync and processing
      if (workItem.CancellationToken.IsCancellationRequested) {
        WhizbangActivitySource.RecordDefensiveCancellation(
          activity,
          "Work item cancelled after queueing but before execution"
        );
        continue; // Skip cancelled work
      }

      try {
        await workItem.ExecuteAsync(workItem.State);
      } catch (Exception ex) {
        // DEFENSIVE: Should never happen - exceptions captured in PooledValueTaskSource
        // Kept as safety net for unexpected exception paths
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

  private readonly struct WorkItem(
    IMessageEnvelope envelope,
    PolicyContext context,
    Func<object?, ValueTask> executeAsync,
    object? state,
    CancellationToken cancellationToken
    ) {
    public readonly IMessageEnvelope Envelope = envelope;
    public readonly PolicyContext Context = context;
    public readonly Func<object?, ValueTask> ExecuteAsync = executeAsync;
    public readonly object? State = state;
    public readonly CancellationToken CancellationToken = cancellationToken;
  }
}
