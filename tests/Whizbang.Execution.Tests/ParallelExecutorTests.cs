using TUnit.Core;
using Whizbang.Core.Execution;

namespace Whizbang.Execution.Tests;

/// <summary>
/// Tests for ParallelExecutor implementation.
/// Inherits contract tests to ensure compliance with IExecutionStrategy requirements.
/// </summary>
[Category("Execution")]
[InheritsTests]
public class ParallelExecutorTests : ExecutionStrategyContractTests {
  /// <summary>
  /// Creates a ParallelExecutor for testing.
  /// </summary>
  protected override IExecutionStrategy CreateStrategy() {
    return new ParallelExecutor(maxConcurrency: 10);
  }

  /// <summary>
  /// ParallelExecutor does NOT guarantee ordering.
  /// </summary>
  protected override bool GuaranteesOrdering => false;
}
