using Whizbang.Core.Observability;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Pooling;

/// <summary>
/// <tests>tests/Whizbang.Execution.Tests/ExecutionStatePoolTests.cs:Initialize_ShouldSetPropertiesAsync</tests>
/// <tests>tests/Whizbang.Execution.Tests/ExecutionStatePoolTests.cs:Reset_ShouldClearPropertiesAsync</tests>
/// Poolable state object that holds execution context for SerialExecutor.
/// Eliminates lambda closure allocations by providing explicit state passing.
/// </summary>
/// <typeparam name="TResult">The result type of the handler</typeparam>
public sealed class ExecutionState<TResult> {
  /// <summary>
  /// The message envelope being processed.
  /// </summary>
  public IMessageEnvelope Envelope { get; private set; } = null!;

  /// <summary>
  /// The policy context for the execution.
  /// </summary>
  public PolicyContext Context { get; private set; } = null!;

  /// <summary>
  /// The handler function to execute.
  /// </summary>
  public Func<IMessageEnvelope, PolicyContext, ValueTask<TResult>> Handler { get; private set; } = null!;

  /// <summary>
  /// The pooled value task source to complete.
  /// </summary>
  public PooledValueTaskSource<TResult> Source { get; private set; } = null!;

  /// <summary>
  /// Initializes the state for a new execution.
  /// Call this before using a pooled instance.
  /// </summary>
  /// <tests>tests/Whizbang.Execution.Tests/ExecutionStatePoolTests.cs:Initialize_ShouldSetPropertiesAsync</tests>
  public void Initialize(
    IMessageEnvelope envelope,
    PolicyContext context,
    Func<IMessageEnvelope, PolicyContext, ValueTask<TResult>> handler,
    PooledValueTaskSource<TResult> source
  ) {
    Envelope = envelope;
    Context = context;
    Handler = handler;
    Source = source;
  }

  /// <summary>
  /// Clears the state before returning to the pool.
  /// Call this before returning to ExecutionStatePool.
  /// </summary>
  /// <tests>tests/Whizbang.Execution.Tests/ExecutionStatePoolTests.cs:Reset_ShouldClearPropertiesAsync</tests>
  public void Reset() {
    Envelope = null!;
    Context = null!;
    Handler = null!;
    Source = null!;
  }
}
