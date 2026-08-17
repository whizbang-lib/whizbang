using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Provides manual flush capability for queued outbox/inbox messages.
/// Inject this when you need explicit control over when messages are persisted,
/// independent of the configured WorkCoordinatorStrategy's automatic flushing.
/// </summary>
/// <docs>data/work-coordinator-strategies</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkFlusherTests.cs:ImmediateStrategy_FlushAsync_DelegatesToStrategyWithRequiredModeAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkFlusherTests.cs:BatchStrategy_FlushAsync_DelegatesToStrategyWithRequiredModeAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkFlusherTests.cs:FlushAsync_WithCancellationToken_PassesThroughAsync</tests>
public interface IWorkFlusher {
  /// <summary>
  /// Immediately flushes all queued messages to the database. Delegates to the
  /// underlying strategy's <c>FlushAndGetBatchAsync</c>, forcing persistence regardless
  /// of the strategy's batching window (used by end-of-request middleware).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkFlusherTests.cs</tests>
  Task FlushAsync(CancellationToken ct = default);
}
