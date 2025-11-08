namespace Whizbang.Core;

/// <summary>
/// Receptors receive messages (commands) and produce responses (events).
/// They are stateless decision-making components that apply business rules
/// and emit events representing decisions made.
/// </summary>
/// <typeparam name="TMessage">The type of message this receptor handles</typeparam>
/// <typeparam name="TResponse">The type of response this receptor produces</typeparam>
public interface IReceptor<in TMessage, TResponse> {
  /// <summary>
  /// Handles a message, applies business logic, and returns a response.
  /// </summary>
  /// <param name="message">The message to process</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The response representing the decision made</returns>
  ValueTask<TResponse> HandleAsync(TMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Receptors receive messages (commands/events) without producing a typed response.
/// This is the zero-allocation pattern for command/event handling where only side effects matter.
/// Use this interface when you don't need to return a business result, enabling optimal performance.
/// </summary>
/// <typeparam name="TMessage">The type of message this receptor handles</typeparam>
public interface IReceptor<in TMessage> {
  /// <summary>
  /// Handles a message and performs side effects without returning a result.
  /// For synchronous operations, return ValueTask.CompletedTask for zero allocations.
  /// </summary>
  /// <param name="message">The message to process</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>A ValueTask representing the async operation (use CompletedTask for sync operations)</returns>
  ValueTask HandleAsync(TMessage message, CancellationToken cancellationToken = default);
}
