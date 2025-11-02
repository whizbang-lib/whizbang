using TUnit.Core;
using Whizbang.Core.Execution;

namespace Whizbang.Execution.Tests;

/// <summary>
/// Tests for SerialExecutor implementation.
/// Inherits contract tests to ensure compliance with IExecutionStrategy requirements.
/// </summary>
[Category("Execution")]
[InheritsTests]
public class SerialExecutorTests : ExecutionStrategyContractTests {
  /// <summary>
  /// Creates a SerialExecutor for testing.
  /// </summary>
  protected override IExecutionStrategy CreateStrategy() {
    return new SerialExecutor();
  }

  /// <summary>
  /// SerialExecutor guarantees strict FIFO ordering.
  /// </summary>
  protected override bool GuaranteesOrdering => true;
}
