using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Whizbang.Core;

/// <summary>
/// The Dispatcher routes messages to appropriate handlers and orchestrates
/// component interactions throughout the system.
/// Provides three distinct dispatch patterns:
/// - SendAsync: Command dispatch with delivery receipt (can work over wire)
/// - LocalInvokeAsync: In-process RPC with typed business result (zero allocation)
/// - PublishAsync: Event broadcasting (fire-and-forget)
/// </summary>
public interface IDispatcher {
  // ========================================
  // SEND PATTERN - Command Dispatch with Acknowledgment
  // ========================================

  /// <summary>
  /// Sends a message and returns a delivery receipt (not the business result).
  /// Use this for async workflows, remote execution, or inbox pattern.
  /// Can work over network transports in future versions.
  /// </summary>
  /// <param name="message">The message to send</param>
  /// <returns>Delivery receipt with correlation information</returns>
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  Task<IDeliveryReceipt> SendAsync(object message);

  /// <summary>
  /// Sends a message with explicit context and returns a delivery receipt.
  /// Captures caller information for debugging and observability.
  /// </summary>
  /// <param name="message">The message to send</param>
  /// <param name="context">The message context</param>
  /// <param name="callerMemberName">Caller method name (auto-captured)</param>
  /// <param name="callerFilePath">Caller file path (auto-captured)</param>
  /// <param name="callerLineNumber">Caller line number (auto-captured)</param>
  /// <returns>Delivery receipt with correlation information</returns>
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  Task<IDeliveryReceipt> SendAsync(
    object message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  );

  // ========================================
  // LOCAL INVOKE PATTERN - In-Process RPC
  // ========================================

  /// <summary>
  /// Invokes a receptor in-process and returns the typed business result.
  /// PERFORMANCE: Zero allocation, target &lt; 20ns per invocation.
  /// RESTRICTION: In-process only - throws InvalidOperationException if used with remote transport.
  /// Use this for high-throughput local workflows where you need immediate typed results.
  /// </summary>
  /// <typeparam name="TResult">The expected business result type</typeparam>
  /// <param name="message">The message to process</param>
  /// <returns>The typed business result from the receptor</returns>
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  ValueTask<TResult> LocalInvokeAsync<TResult>(object message);

  /// <summary>
  /// Invokes a receptor in-process with explicit context and returns the typed business result.
  /// Captures caller information for debugging and observability.
  /// </summary>
  /// <typeparam name="TResult">The expected business result type</typeparam>
  /// <param name="message">The message to process</param>
  /// <param name="context">The message context</param>
  /// <param name="callerMemberName">Caller method name (auto-captured)</param>
  /// <param name="callerFilePath">Caller file path (auto-captured)</param>
  /// <param name="callerLineNumber">Caller line number (auto-captured)</param>
  /// <returns>The typed business result from the receptor</returns>
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  ValueTask<TResult> LocalInvokeAsync<TResult>(
    object message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  );

  /// <summary>
  /// Invokes a void receptor in-process without returning a business result.
  /// PERFORMANCE: Zero allocation target for command/event patterns.
  /// RESTRICTION: In-process only - throws InvalidOperationException if used with remote transport.
  /// Use this for high-throughput command/event handling where side effects matter but results don't.
  /// </summary>
  /// <param name="message">The message to process</param>
  /// <returns>ValueTask representing the completion (CompletedTask for sync operations)</returns>
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  ValueTask LocalInvokeAsync(object message);

  /// <summary>
  /// Invokes a void receptor in-process with explicit context without returning a business result.
  /// Captures caller information for debugging and observability.
  /// </summary>
  /// <param name="message">The message to process</param>
  /// <param name="context">The message context</param>
  /// <param name="callerMemberName">Caller method name (auto-captured)</param>
  /// <param name="callerFilePath">Caller file path (auto-captured)</param>
  /// <param name="callerLineNumber">Caller line number (auto-captured)</param>
  /// <returns>ValueTask representing the completion (CompletedTask for sync operations)</returns>
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  ValueTask LocalInvokeAsync(
    object message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  );

  // ========================================
  // PUBLISH PATTERN - Event Broadcasting
  // ========================================

  /// <summary>
  /// Publishes an event to all interested handlers (fire-and-forget).
  /// No return value - handlers execute independently.
  /// </summary>
  /// <typeparam name="TEvent">The event type</typeparam>
  /// <param name="event">The event to publish</param>
  [RequiresUnreferencedCode("Event types and handlers are resolved using runtime type information. For AOT compatibility, ensure all event types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Event publishing uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  Task PublishAsync<TEvent>(TEvent @event);

  // ========================================
  // BATCH OPERATIONS
  // ========================================

  /// <summary>
  /// Sends multiple messages and collects all delivery receipts.
  /// </summary>
  /// <param name="messages">The messages to send</param>
  /// <returns>All delivery receipts</returns>
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages);

  /// <summary>
  /// Invokes multiple receptors in-process and collects all typed business results.
  /// RESTRICTION: In-process only - throws InvalidOperationException if used with remote transport.
  /// </summary>
  /// <typeparam name="TResult">The expected business result type</typeparam>
  /// <param name="messages">The messages to process</param>
  /// <returns>All typed business results from receptors</returns>
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages);
}
