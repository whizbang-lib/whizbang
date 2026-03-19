using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Provides manual flush capability for queued outbox/inbox messages.
/// Inject this when you need explicit control over when messages are persisted,
/// independent of the configured WorkCoordinatorStrategy's automatic flushing.
/// </summary>
/// <docs>data/work-coordinator-strategies</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WorkFlusherTests.cs</tests>
public interface IWorkFlusher {
  /// <summary>
  /// Immediately flushes all queued messages to the database.
  /// Equivalent to calling FlushAsync with FlushMode.Required on the underlying strategy.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/WorkFlusherTests.cs:ImmediateStrategy_FlushAsync_DelegatesToStrategyWithRequiredModeAsync</tests>
  Task FlushAsync(CancellationToken ct = default);
}
