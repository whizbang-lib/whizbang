using Whizbang.Core.Observability;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Execution;

/// <summary>
/// Defines a strategy for executing message handlers.
/// Implementations control ordering, concurrency, and lifecycle.
/// </summary>
/// <docs>components/dispatcher</docs>
/// <tests>tests/Whizbang.Execution.Tests/ExecutionStrategyContractTests.cs</tests>
public interface IExecutionStrategy {
  /// <summary>
  /// Name of the execution strategy (e.g., "Serial", "Parallel")
  /// </summary>
  /// <tests>tests/Whizbang.Execution.Tests/ExecutionStrategyContractTests.cs:Name_ShouldNotBeEmptyAsync</tests>
  string Name { get; }

  /// <summary>
  /// Executes a message handler with the given envelope and context.
  /// Returns ValueTask for zero-allocation async when handlers complete synchronously.
  /// </summary>
  /// <tests>tests/Whizbang.Execution.Tests/ExecutionStrategyContractTests.cs:ExecuteAsync_ShouldCallHandlerAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/ExecutionStrategyContractTests.cs:ExecuteAsync_ShouldReturnHandlerResultAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/ExecutionStrategyContractTests.cs:ExecuteAsync_ShouldPassEnvelopeToHandlerAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/ExecutionStrategyContractTests.cs:ExecuteAsync_ShouldPropagateHandlerExceptionAsync</tests>
  /// <tests>tests/Whizbang.Execution.Tests/ExecutionStrategyContractTests.cs:ExecuteAsync_ShouldPropagateCancellationAsync</tests>
  ValueTask<TResult> ExecuteAsync<TResult>(
    IMessageEnvelope envelope,
    Func<IMessageEnvelope, PolicyContext, ValueTask<TResult>> handler,
    PolicyContext context,
    CancellationToken ct = default
  );

  /// <summary>
  /// Starts the execution strategy (initializes any background workers/channels)
  /// </summary>
  /// <tests>tests/Whizbang.Execution.Tests/ExecutionStrategyContractTests.cs:StartAsync_ShouldBeIdempotentAsync</tests>
  Task StartAsync(CancellationToken ct = default);

  /// <summary>
  /// Stops the execution strategy (stops accepting new work)
  /// </summary>
  /// <tests>tests/Whizbang.Execution.Tests/ExecutionStrategyContractTests.cs:StopAsync_ShouldPreventNewExecutionsAsync</tests>
  Task StopAsync(CancellationToken ct = default);

  /// <summary>
  /// Drains any pending work and waits for completion
  /// </summary>
  /// <tests>tests/Whizbang.Execution.Tests/ExecutionStrategyContractTests.cs:DrainAsync_ShouldWaitForPendingWorkAsync</tests>
  Task DrainAsync(CancellationToken ct = default);
}
