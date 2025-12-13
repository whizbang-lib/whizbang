using Whizbang.Core.Observability;
using Whizbang.Core.Policies;

namespace Whizbang.Core.Execution;

/// <summary>
/// Defines a strategy for executing message handlers.
/// Implementations control ordering, concurrency, and lifecycle.
/// </summary>
/// <docs>components/dispatcher</docs>
public interface IExecutionStrategy {
  /// <summary>
  /// Name of the execution strategy (e.g., "Serial", "Parallel")
  /// </summary>
  string Name { get; }

  /// <summary>
  /// Executes a message handler with the given envelope and context.
  /// Returns ValueTask for zero-allocation async when handlers complete synchronously.
  /// </summary>
  ValueTask<TResult> ExecuteAsync<TResult>(
    IMessageEnvelope envelope,
    Func<IMessageEnvelope, PolicyContext, ValueTask<TResult>> handler,
    PolicyContext context,
    CancellationToken ct = default
  );

  /// <summary>
  /// Starts the execution strategy (initializes any background workers/channels)
  /// </summary>
  Task StartAsync(CancellationToken ct = default);

  /// <summary>
  /// Stops the execution strategy (stops accepting new work)
  /// </summary>
  Task StopAsync(CancellationToken ct = default);

  /// <summary>
  /// Drains any pending work and waits for completion
  /// </summary>
  Task DrainAsync(CancellationToken ct = default);
}
