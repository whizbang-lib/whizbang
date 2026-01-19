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
/// <docs>core-concepts/dispatcher</docs>
public interface IDispatcher {
  // ========================================
  // SEND PATTERN - Command Dispatch with Acknowledgment
  // ========================================

  /// <summary>
  /// Sends a typed message and returns a delivery receipt (AOT-compatible).
  /// Use this for async workflows, remote execution, or inbox pattern.
  /// Can work over network transports in future versions.
  /// </summary>
  /// <typeparam name="TMessage">The message type</typeparam>
  /// <param name="message">The message to send</param>
  /// <returns>Delivery receipt with correlation information</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:Send_WithValidMessage_ShouldReturnDeliveryReceiptAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:Send_WithUnknownMessageType_ShouldThrowHandlerNotFoundExceptionAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:SendAsync_Generic_CreatesTypedEnvelopeForTracingAsync</tests>
  Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull;

  /// <summary>
  /// Sends a message and returns a delivery receipt (not the business result).
  /// Use this for async workflows, remote execution, or inbox pattern.
  /// Can work over network transports in future versions.
  /// For AOT compatibility, use the generic overload SendAsync&lt;TMessage&gt;.
  /// </summary>
  /// <param name="message">The message to send</param>
  /// <returns>Delivery receipt with correlation information</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:Dispatcher_MessageContext_ShouldGenerateUniqueMessageIdsAsync</tests>
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
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:Send_WithContext_ShouldPreserveCorrelationIdInReceiptAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:Dispatcher_ShouldTrackCausationChainInReceiptAsync</tests>
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
  /// Invokes a receptor in-process with typed message and returns the typed business result (AOT-compatible).
  /// PERFORMANCE: Zero allocation, target &lt; 20ns per invocation.
  /// RESTRICTION: In-process only - throws InvalidOperationException if used with remote transport.
  /// Use this for high-throughput local workflows where you need immediate typed results.
  /// </summary>
  /// <typeparam name="TMessage">The message type</typeparam>
  /// <typeparam name="TResult">The expected business result type</typeparam>
  /// <param name="message">The message to process</param>
  /// <returns>The typed business result from the receptor</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvoke_WithValidMessage_ShouldReturnBusinessResultAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvoke_WithUnknownMessageType_ShouldThrowHandlerNotFoundExceptionAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_DoesNotRequireTypePreservation_ForInProcessRPCAsync</tests>
  ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull;

  /// <summary>
  /// Invokes a receptor in-process and returns the typed business result.
  /// PERFORMANCE: Zero allocation, target &lt; 20ns per invocation.
  /// RESTRICTION: In-process only - throws InvalidOperationException if used with remote transport.
  /// Use this for high-throughput local workflows where you need immediate typed results.
  /// For AOT compatibility, use the generic overload LocalInvokeAsync&lt;TMessage, TResult&gt;.
  /// </summary>
  /// <typeparam name="TResult">The expected business result type</typeparam>
  /// <param name="message">The message to process</param>
  /// <returns>The typed business result from the receptor</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvoke_WithValidMessage_ShouldReturnBusinessResultAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:Dispatcher_ShouldRouteToCorrectHandlerAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:Dispatcher_MultipleReceptorsSameMessage_ShouldRouteToAllAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_DoesNotRequireTypePreservation_ForInProcessRPCAsync</tests>
  ValueTask<TResult> LocalInvokeAsync<TResult>(object message);

  /// <summary>
  /// Invokes a receptor in-process with typed message and explicit context, returning the typed business result (AOT-compatible).
  /// Captures caller information for debugging and observability.
  /// Type information is preserved at compile time, avoiding reflection.
  /// </summary>
  /// <typeparam name="TMessage">The message type</typeparam>
  /// <typeparam name="TResult">The expected business result type</typeparam>
  /// <param name="message">The message to process</param>
  /// <param name="context">The message context</param>
  /// <param name="callerMemberName">Caller method name (auto-captured)</param>
  /// <param name="callerFilePath">Caller file path (auto-captured)</param>
  /// <param name="callerLineNumber">Caller line number (auto-captured)</param>
  /// <returns>The typed business result from the receptor</returns>
  ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(
    TMessage message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) where TMessage : notnull;

  /// <summary>
  /// Invokes a receptor in-process with explicit context and returns the typed business result.
  /// Captures caller information for debugging and observability.
  /// For AOT compatibility, use the generic overload LocalInvokeAsync&lt;TMessage, TResult&gt;.
  /// </summary>
  /// <typeparam name="TResult">The expected business result type</typeparam>
  /// <param name="message">The message to process</param>
  /// <param name="context">The message context</param>
  /// <param name="callerMemberName">Caller method name (auto-captured)</param>
  /// <param name="callerFilePath">Caller file path (auto-captured)</param>
  /// <param name="callerLineNumber">Caller line number (auto-captured)</param>
  /// <returns>The typed business result from the receptor</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvoke_WithContext_ShouldPreserveContextAsync</tests>
  ValueTask<TResult> LocalInvokeAsync<TResult>(
    object message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  );

  /// <summary>
  /// Invokes a void receptor in-process with typed message without returning a business result (AOT-compatible).
  /// PERFORMANCE: Zero allocation target for command/event patterns.
  /// RESTRICTION: In-process only - throws InvalidOperationException if used with remote transport.
  /// Use this for high-throughput command/event handling where side effects matter but results don't.
  /// </summary>
  /// <typeparam name="TMessage">The message type</typeparam>
  /// <param name="message">The message to process</param>
  /// <returns>ValueTask representing the completion (CompletedTask for sync operations)</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_VoidReceptor_MultipleInvocations_ShouldTrackAllAsync</tests>
  ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull;

  /// <summary>
  /// Invokes a void receptor in-process without returning a business result.
  /// PERFORMANCE: Zero allocation target for command/event patterns.
  /// RESTRICTION: In-process only - throws InvalidOperationException if used with remote transport.
  /// Use this for high-throughput command/event handling where side effects matter but results don't.
  /// For AOT compatibility, use the generic overload LocalInvokeAsync&lt;TMessage&gt;.
  /// </summary>
  /// <param name="message">The message to process</param>
  /// <returns>ValueTask representing the completion (CompletedTask for sync operations)</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_VoidReceptor_ShouldInvokeWithoutReturningResultAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_VoidReceptor_SynchronousCompletion_ShouldNotAllocateAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_VoidReceptor_AsynchronousCompletion_ShouldCompleteAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_VoidReceptor_NoHandler_ShouldThrowHandlerNotFoundExceptionAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_VoidReceptor_WithTracing_StoresEnvelopeAsync</tests>
  ValueTask LocalInvokeAsync(object message);

  /// <summary>
  /// Invokes a void receptor in-process with typed message and explicit context without returning a business result (AOT-compatible).
  /// Captures caller information for debugging and observability.
  /// Type information is preserved at compile time, avoiding reflection.
  /// </summary>
  /// <typeparam name="TMessage">The message type</typeparam>
  /// <param name="message">The message to process</param>
  /// <param name="context">The message context</param>
  /// <param name="callerMemberName">Caller method name (auto-captured)</param>
  /// <param name="callerFilePath">Caller file path (auto-captured)</param>
  /// <param name="callerLineNumber">Caller line number (auto-captured)</param>
  /// <returns>ValueTask representing the completion (CompletedTask for sync operations)</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_VoidReceptor_WithContext_ShouldAcceptContextAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_VoidReceptor_WithNullContext_ThrowsArgumentNullExceptionAsync</tests>
  ValueTask LocalInvokeAsync<TMessage>(
    TMessage message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) where TMessage : notnull;

  /// <summary>
  /// Invokes a void receptor in-process with explicit context without returning a business result.
  /// Captures caller information for debugging and observability.
  /// For AOT compatibility, use the generic overload LocalInvokeAsync&lt;TMessage&gt;.
  /// </summary>
  /// <param name="message">The message to process</param>
  /// <param name="context">The message context</param>
  /// <param name="callerMemberName">Caller method name (auto-captured)</param>
  /// <param name="callerFilePath">Caller file path (auto-captured)</param>
  /// <param name="callerLineNumber">Caller line number (auto-captured)</param>
  /// <returns>ValueTask representing the completion (CompletedTask for sync operations)</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_WithNullContext_ThrowsArgumentNullExceptionAsync</tests>
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
  /// <param name="eventData">The event to publish</param>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:Publish_WithEvent_ShouldNotifyAllHandlersAsync</tests>
  Task PublishAsync<TEvent>(TEvent eventData);

  // ========================================
  // BATCH OPERATIONS
  // ========================================

  /// <summary>
  /// Sends multiple typed messages and collects all delivery receipts (AOT-compatible).
  /// Type information is preserved at compile time, avoiding reflection.
  /// </summary>
  /// <typeparam name="TMessage">The message type</typeparam>
  /// <param name="messages">The messages to send</param>
  /// <returns>All delivery receipts</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:SendManyAsync_Generic_CreatesTypedEnvelopesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:SendManyAsync_Generic_DifferentFromNonGenericVersionAsync</tests>
  Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull;

  /// <summary>
  /// Sends multiple messages and collects all delivery receipts.
  /// For AOT compatibility, use the generic overload SendManyAsync&lt;TMessage&gt;.
  /// </summary>
  /// <param name="messages">The messages to send</param>
  /// <returns>All delivery receipts</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:SendMany_WithMultipleCommands_ShouldReturnAllReceiptsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:SendManyAsync_Generic_DifferentFromNonGenericVersionAsync</tests>
  Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages);

  /// <summary>
  /// Invokes multiple receptors in-process and collects all typed business results.
  /// RESTRICTION: In-process only - throws InvalidOperationException if used with remote transport.
  /// </summary>
  /// <typeparam name="TResult">The expected business result type</typeparam>
  /// <param name="messages">The messages to process</param>
  /// <returns>All typed business results from receptors</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeMany_WithMultipleCommands_ShouldReturnAllResultsAsync</tests>
  ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages);
}
