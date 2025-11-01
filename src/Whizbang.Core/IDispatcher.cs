namespace Whizbang.Core;

/// <summary>
/// The Dispatcher routes messages to appropriate handlers and orchestrates
/// component interactions throughout the system.
/// </summary>
public interface IDispatcher {
  /// <summary>
  /// Sends a message to a receptor and waits for the response.
  /// </summary>
  /// <typeparam name="TResult">The expected result type</typeparam>
  /// <param name="message">The message to send</param>
  /// <returns>The result from the receptor</returns>
  Task<TResult> Send<TResult>(object message);

  /// <summary>
  /// Sends a message with explicit context.
  /// </summary>
  /// <typeparam name="TResult">The expected result type</typeparam>
  /// <param name="message">The message to send</param>
  /// <param name="context">The message context</param>
  /// <returns>The result from the receptor</returns>
  Task<TResult> Send<TResult>(object message, IMessageContext context);

  /// <summary>
  /// Publishes an event to all interested handlers.
  /// </summary>
  /// <typeparam name="TEvent">The event type</typeparam>
  /// <param name="event">The event to publish</param>
  Task Publish<TEvent>(TEvent @event);

  /// <summary>
  /// Sends multiple messages and collects all responses.
  /// </summary>
  /// <typeparam name="TResult">The expected result type</typeparam>
  /// <param name="messages">The messages to send</param>
  /// <returns>All results from receptors</returns>
  Task<IEnumerable<TResult>> SendMany<TResult>(IEnumerable<object> messages);
}
