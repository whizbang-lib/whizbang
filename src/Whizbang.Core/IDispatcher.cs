using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

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
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  Task<TResult> SendAsync<TResult>(object message);

  /// <summary>
  /// Sends a message with explicit context.
  /// Captures caller information for debugging and observability.
  /// </summary>
  /// <typeparam name="TResult">The expected result type</typeparam>
  /// <param name="message">The message to send</param>
  /// <param name="context">The message context</param>
  /// <param name="callerMemberName">Caller method name (auto-captured)</param>
  /// <param name="callerFilePath">Caller file path (auto-captured)</param>
  /// <param name="callerLineNumber">Caller line number (auto-captured)</param>
  /// <returns>The result from the receptor</returns>
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  Task<TResult> SendAsync<TResult>(
    object message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  );

  /// <summary>
  /// Publishes an event to all interested handlers.
  /// </summary>
  /// <typeparam name="TEvent">The event type</typeparam>
  /// <param name="event">The event to publish</param>
  [RequiresUnreferencedCode("Event types and handlers are resolved using runtime type information. For AOT compatibility, ensure all event types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Event publishing uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  Task PublishAsync<TEvent>(TEvent @event);

  /// <summary>
  /// Sends multiple messages and collects all responses.
  /// </summary>
  /// <typeparam name="TResult">The expected result type</typeparam>
  /// <param name="messages">The messages to send</param>
  /// <returns>All results from receptors</returns>
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  Task<IEnumerable<TResult>> SendManyAsync<TResult>(IEnumerable<object> messages);
}
