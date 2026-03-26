using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Observability;

namespace Whizbang.Core;

/// <summary>
/// The Dispatcher routes messages to appropriate handlers and orchestrates
/// component interactions throughout the system.
/// Provides three distinct dispatch patterns:
/// - SendAsync: Command dispatch with delivery receipt (can work over wire)
/// - LocalInvokeAsync: In-process RPC with typed business result (zero allocation)
/// - PublishAsync: Event broadcasting (fire-and-forget)
/// </summary>
/// <docs>fundamentals/dispatcher/dispatcher</docs>
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
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:Send_WithUnknownMessageType_ShouldThrowReceptorNotFoundExceptionAsync</tests>
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

  /// <summary>
  /// Sends a typed message with dispatch options and returns a delivery receipt (AOT-compatible).
  /// </summary>
  /// <typeparam name="TMessage">The message type</typeparam>
  /// <param name="message">The message to send</param>
  /// <param name="options">Options controlling dispatch behavior (cancellation, timeout)</param>
  /// <returns>Delivery receipt with correlation information</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:SendAsync_WithDispatchOptions_ReturnsDeliveryReceiptAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:SendAsync_WithDispatchOptions_Generic_PreservesTypeAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:SendAsync_WithCancelledToken_ThrowsOperationCanceledExceptionAsync</tests>
  Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, DispatchOptions options) where TMessage : notnull;

  /// <summary>
  /// Sends a message with dispatch options and returns a delivery receipt.
  /// </summary>
  /// <param name="message">The message to send</param>
  /// <param name="options">Options controlling dispatch behavior (cancellation, timeout)</param>
  /// <returns>Delivery receipt with correlation information</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:SendAsync_WithDefaultOptions_BehavesSameAsWithoutOptionsAsync</tests>
  Task<IDeliveryReceipt> SendAsync(object message, DispatchOptions options);

  /// <summary>
  /// Sends a message with explicit context and dispatch options.
  /// </summary>
  /// <param name="message">The message to send</param>
  /// <param name="context">The message context</param>
  /// <param name="options">Options controlling dispatch behavior (cancellation, timeout)</param>
  /// <param name="callerMemberName">Caller method name (auto-captured)</param>
  /// <param name="callerFilePath">Caller file path (auto-captured)</param>
  /// <param name="callerLineNumber">Caller line number (auto-captured)</param>
  /// <returns>Delivery receipt with correlation information</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:SendAsync_WithContext_AndDispatchOptions_PreservesCorrelationAsync</tests>
  Task<IDeliveryReceipt> SendAsync(
    object message,
    IMessageContext context,
    DispatchOptions options,
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
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvoke_WithUnknownMessageType_ShouldThrowReceptorNotFoundExceptionAsync</tests>
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
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_VoidReceptor_NoHandler_ShouldThrowReceptorNotFoundExceptionAsync</tests>
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

  /// <summary>
  /// Invokes a receptor in-process with dispatch options and returns the typed business result.
  /// </summary>
  /// <typeparam name="TResult">The expected business result type</typeparam>
  /// <param name="message">The message to process</param>
  /// <param name="options">Options controlling dispatch behavior (cancellation, timeout)</param>
  /// <returns>The typed business result from the receptor</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_WithDispatchOptions_ReturnsResultAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_WithCancelledToken_ThrowsOperationCanceledExceptionAsync</tests>
  ValueTask<TResult> LocalInvokeAsync<TResult>(object message, DispatchOptions options);

  /// <summary>
  /// Invokes a void receptor in-process with dispatch options.
  /// </summary>
  /// <param name="message">The message to process</param>
  /// <param name="options">Options controlling dispatch behavior (cancellation, timeout)</param>
  /// <returns>ValueTask representing the completion</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_Void_WithDispatchOptions_CompletesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_Void_WithCancelledToken_ThrowsAsync</tests>
  ValueTask LocalInvokeAsync(object message, DispatchOptions options);

  // ========================================
  // LOCAL INVOKE WITH RECEIPT — In-Process RPC with Dispatch Metadata
  // ========================================

  /// <summary>
  /// Invokes a receptor in-process with typed message and returns both the typed business result
  /// AND a delivery receipt with dispatch metadata (AOT-compatible).
  /// Always takes the tracing path since envelope data is needed for the receipt.
  /// </summary>
  /// <typeparam name="TMessage">The message type</typeparam>
  /// <typeparam name="TResult">The expected business result type</typeparam>
  /// <param name="message">The message to process</param>
  /// <returns>An <see cref="InvokeResult{TResult}"/> containing both the business result and delivery receipt.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherInvokeWithReceiptTests.cs:LocalInvokeWithReceipt_Generic_ReturnsBusinessResultAndReceiptAsync</tests>
  /// <docs>fundamentals/dispatcher/dispatcher#local-invoke-with-receipt</docs>
  ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(
      TMessage message) where TMessage : notnull;

  /// <summary>
  /// Invokes a receptor in-process and returns both the typed business result
  /// AND a delivery receipt with dispatch metadata.
  /// Always takes the tracing path since envelope data is needed for the receipt.
  /// For AOT compatibility, use the generic overload LocalInvokeWithReceiptAsync&lt;TMessage, TResult&gt;.
  /// </summary>
  /// <typeparam name="TResult">The expected business result type</typeparam>
  /// <param name="message">The message to process</param>
  /// <returns>An <see cref="InvokeResult{TResult}"/> containing both the business result and delivery receipt.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherInvokeWithReceiptTests.cs:LocalInvokeWithReceipt_ReturnsBusinessResultAndReceiptAsync</tests>
  /// <docs>fundamentals/dispatcher/dispatcher#local-invoke-with-receipt</docs>
  ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(
      object message);

  /// <summary>
  /// Invokes a receptor in-process with typed message and explicit context, returning both
  /// the typed business result AND a delivery receipt (AOT-compatible).
  /// Captures caller information for debugging and observability.
  /// </summary>
  /// <typeparam name="TMessage">The message type</typeparam>
  /// <typeparam name="TResult">The expected business result type</typeparam>
  /// <param name="message">The message to process</param>
  /// <param name="context">The message context</param>
  /// <param name="callerMemberName">Caller method name (auto-captured)</param>
  /// <param name="callerFilePath">Caller file path (auto-captured)</param>
  /// <param name="callerLineNumber">Caller line number (auto-captured)</param>
  /// <returns>An <see cref="InvokeResult{TResult}"/> containing both the business result and delivery receipt.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherInvokeWithReceiptTests.cs:LocalInvokeWithReceipt_WithContext_PreservesCorrelationIdAsync</tests>
  /// <docs>fundamentals/dispatcher/dispatcher#local-invoke-with-receipt</docs>
  ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(
      TMessage message,
      IMessageContext context,
      [CallerMemberName] string callerMemberName = "",
      [CallerFilePath] string callerFilePath = "",
      [CallerLineNumber] int callerLineNumber = 0)
      where TMessage : notnull;

  /// <summary>
  /// Invokes a receptor in-process with explicit context and returns both the typed business result
  /// AND a delivery receipt.
  /// For AOT compatibility, use the generic overload LocalInvokeWithReceiptAsync&lt;TMessage, TResult&gt;.
  /// </summary>
  /// <typeparam name="TResult">The expected business result type</typeparam>
  /// <param name="message">The message to process</param>
  /// <param name="context">The message context</param>
  /// <param name="callerMemberName">Caller method name (auto-captured)</param>
  /// <param name="callerFilePath">Caller file path (auto-captured)</param>
  /// <param name="callerLineNumber">Caller line number (auto-captured)</param>
  /// <returns>An <see cref="InvokeResult{TResult}"/> containing both the business result and delivery receipt.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherInvokeWithReceiptTests.cs:LocalInvokeWithReceipt_WithContext_NonGeneric_PreservesCorrelationIdAsync</tests>
  /// <docs>fundamentals/dispatcher/dispatcher#local-invoke-with-receipt</docs>
  ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(
      object message,
      IMessageContext context,
      [CallerMemberName] string callerMemberName = "",
      [CallerFilePath] string callerFilePath = "",
      [CallerLineNumber] int callerLineNumber = 0);

  /// <summary>
  /// Invokes a receptor in-process with dispatch options and returns both the typed business result
  /// AND a delivery receipt.
  /// </summary>
  /// <typeparam name="TResult">The expected business result type</typeparam>
  /// <param name="message">The message to process</param>
  /// <param name="options">Options controlling dispatch behavior (cancellation, timeout)</param>
  /// <returns>An <see cref="InvokeResult{TResult}"/> containing both the business result and delivery receipt.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherInvokeWithReceiptTests.cs:LocalInvokeWithReceipt_WithDispatchOptions_ReturnsReceiptAsync</tests>
  /// <docs>fundamentals/dispatcher/dispatcher#local-invoke-with-receipt</docs>
  ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(
      object message, DispatchOptions options);

  // ========================================
  // PUBLISH PATTERN - Event Broadcasting
  // ========================================

  /// <summary>
  /// Publishes an event to all interested handlers.
  /// Returns a delivery receipt with StreamId extracted from [StreamId] attribute.
  /// </summary>
  /// <typeparam name="TEvent">The event type</typeparam>
  /// <param name="eventData">The event to publish</param>
  /// <returns>Delivery receipt with correlation information and StreamId</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:Publish_WithEvent_ShouldNotifyAllHandlersAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherDeliveryReceiptTests.cs:PublishAsync_EventWithStreamId_DeliveryReceiptHasStreamIdAsync</tests>
  Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData);

  /// <summary>
  /// Publishes an event with dispatch options.
  /// Returns a delivery receipt with StreamId extracted from [StreamId] attribute.
  /// </summary>
  /// <typeparam name="TEvent">The event type</typeparam>
  /// <param name="eventData">The event to publish</param>
  /// <param name="options">Options controlling dispatch behavior (cancellation, timeout)</param>
  /// <returns>Delivery receipt with correlation information and StreamId</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:PublishAsync_WithDispatchOptions_CompletesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:PublishAsync_WithCancelledToken_ThrowsOperationCanceledExceptionAsync</tests>
  Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options);

  /// <summary>
  /// Cascades a message (event or command) with explicit routing mode.
  /// Called by <see cref="IEventCascader"/> after resolving routing from wrappers and attributes.
  /// </summary>
  /// <param name="message">The message to cascade.</param>
  /// <param name="sourceEnvelope">
  /// The source envelope that caused this cascade (e.g., the command envelope).
  /// Used to inherit SecurityContext for the cascaded message when ambient context is unavailable.
  /// </param>
  /// <param name="mode">The dispatch mode (Local, Outbox, or Both).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task representing the asynchronous operation.</returns>
  /// <remarks>
  /// <para>
  /// Actions based on mode:
  /// - Local: Invokes in-process receptors only
  /// - Outbox: Writes to outbox for cross-service delivery only
  /// - Both: Does both local invocation and outbox write
  /// </para>
  /// <para>
  /// Security context inheritance: The cascaded message gets its own new envelope.
  /// The SecurityContext in the new envelope's initial hop is inherited from the
  /// sourceEnvelope's current security context when ambient context is unavailable.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/dispatcher/dispatcher#cascade-to-outbox</docs>
  Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, Dispatch.DispatchModes mode, CancellationToken cancellationToken = default);

  // ========================================
  // LOCAL INVOKE AND SYNC - Wait for All Perspectives
  // ========================================

  /// <summary>
  /// Invokes a receptor in-process and waits for ALL perspectives to fully process
  /// any events emitted during the invocation before returning the result.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This method combines <see cref="LocalInvokeAsync{TMessage,TResult}(TMessage)"/> with
  /// automatic synchronization. After the handler completes, it waits for all registered
  /// perspectives to process any events that were tracked during the invocation.
  /// </para>
  /// <para>
  /// <strong>Use this when:</strong>
  /// </para>
  /// <list type="bullet">
  ///   <item><description>You need to query read models immediately after a command</description></item>
  ///   <item><description>Building synchronous-feeling APIs over event sourcing</description></item>
  ///   <item><description>API endpoints that must return consistent data after mutations</description></item>
  /// </list>
  /// <para>
  /// <strong>Example:</strong>
  /// </para>
  /// <code>
  /// // In a GraphQL mutation or API controller
  /// var result = await dispatcher.LocalInvokeAndSyncAsync&lt;CreateOrder, OrderResult&gt;(
  ///     new CreateOrder { CustomerId = id, Items = items },
  ///     timeout: TimeSpan.FromSeconds(10));
  ///
  /// // All perspectives have now processed the events - safe to query read models
  /// return result.OrderId;
  /// </code>
  /// </remarks>
  /// <typeparam name="TMessage">The message type.</typeparam>
  /// <typeparam name="TResult">The expected business result type.</typeparam>
  /// <param name="message">The message to process.</param>
  /// <param name="timeout">Maximum time to wait for perspectives to sync. Defaults to 30 seconds.</param>
  /// <param name="onWaiting">
  /// Optional callback invoked when waiting begins. Only called if there are events to wait for
  /// and they haven't already been processed. Not called for <see cref="Perspectives.Sync.SyncOutcome.NoPendingEvents"/>.
  /// </param>
  /// <param name="onDecisionMade">
  /// Optional callback always invoked when the sync decision is made, regardless of outcome.
  /// </param>
  /// <param name="cancellationToken">A cancellation token.</param>
  /// <returns>The typed business result from the receptor.</returns>
  /// <exception cref="TimeoutException">
  /// Thrown when perspectives don't complete processing within the timeout period.
  /// Note: The handler has already completed successfully; only perspective sync timed out.
  /// </exception>
  /// <docs>fundamentals/dispatcher/dispatcher#local-invoke-and-sync</docs>
  Task<TResult> LocalInvokeAndSyncAsync<TMessage, TResult>(
      TMessage message,
      TimeSpan? timeout = null,
      Action<Perspectives.Sync.SyncWaitingContext>? onWaiting = null,
      Action<Perspectives.Sync.SyncDecisionContext>? onDecisionMade = null,
      CancellationToken cancellationToken = default)
      where TMessage : notnull
      => throw new NotSupportedException("LocalInvokeAndSyncAsync requires a Dispatcher implementation with IEventCompletionAwaiter support.");

  /// <summary>
  /// Invokes a void receptor in-process and waits for ALL perspectives to fully process
  /// any events emitted during the invocation.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This method combines <see cref="LocalInvokeAsync{TMessage}(TMessage)"/> with
  /// automatic synchronization. After the handler completes, it waits for all registered
  /// perspectives to process any events that were tracked during the invocation.
  /// </para>
  /// <para>
  /// Returns a <see cref="Perspectives.Sync.SyncResult"/> indicating whether all perspectives
  /// completed processing within the timeout.
  /// </para>
  /// </remarks>
  /// <typeparam name="TMessage">The message type.</typeparam>
  /// <param name="message">The message to process.</param>
  /// <param name="timeout">Maximum time to wait for perspectives to sync. Defaults to 30 seconds.</param>
  /// <param name="onWaiting">
  /// Optional callback invoked when waiting begins. Only called if there are events to wait for
  /// and they haven't already been processed. Not called for <see cref="Perspectives.Sync.SyncOutcome.NoPendingEvents"/>.
  /// </param>
  /// <param name="onDecisionMade">
  /// Optional callback always invoked when the sync decision is made, regardless of outcome.
  /// </param>
  /// <param name="cancellationToken">A cancellation token.</param>
  /// <returns>A <see cref="Perspectives.Sync.SyncResult"/> indicating sync outcome.</returns>
  /// <docs>fundamentals/dispatcher/dispatcher#local-invoke-and-sync</docs>
  Task<Perspectives.Sync.SyncResult> LocalInvokeAndSyncAsync<TMessage>(
      TMessage message,
      TimeSpan? timeout = null,
      Action<Perspectives.Sync.SyncWaitingContext>? onWaiting = null,
      Action<Perspectives.Sync.SyncDecisionContext>? onDecisionMade = null,
      CancellationToken cancellationToken = default)
      where TMessage : notnull
      => throw new NotSupportedException("LocalInvokeAndSyncAsync requires a Dispatcher implementation with IEventCompletionAwaiter support.");

  /// <summary>
  /// Invokes a receptor returning a result and waits for a SPECIFIC perspective to process
  /// any events emitted during the invocation.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Unlike <see cref="LocalInvokeAndSyncAsync{TMessage,TResult}(TMessage,TimeSpan?,Action{Perspectives.Sync.SyncWaitingContext}?,Action{Perspectives.Sync.SyncDecisionContext}?,CancellationToken)"/>
  /// which waits for ALL perspectives, this method waits only for the specified perspective type.
  /// This is useful when you only care about one read model being updated before returning.
  /// </para>
  /// </remarks>
  /// <typeparam name="TMessage">The message type.</typeparam>
  /// <typeparam name="TResult">The expected business result type.</typeparam>
  /// <typeparam name="TPerspective">The perspective type to wait for.</typeparam>
  /// <param name="message">The message to process.</param>
  /// <param name="timeout">Maximum time to wait for the perspective to sync. Defaults to 30 seconds.</param>
  /// <param name="onWaiting">Optional callback invoked when waiting begins.</param>
  /// <param name="onDecisionMade">Optional callback always invoked when the sync decision is made.</param>
  /// <param name="cancellationToken">A cancellation token.</param>
  /// <returns>The typed business result from the receptor.</returns>
  /// <exception cref="TimeoutException">Thrown when the perspective doesn't complete processing within the timeout.</exception>
  /// <docs>fundamentals/dispatcher/dispatcher#local-invoke-and-sync-perspective</docs>
  Task<TResult> LocalInvokeAndSyncAsync<TMessage, TResult, TPerspective>(
      TMessage message,
      TimeSpan? timeout = null,
      Action<Perspectives.Sync.SyncWaitingContext>? onWaiting = null,
      Action<Perspectives.Sync.SyncDecisionContext>? onDecisionMade = null,
      CancellationToken cancellationToken = default)
      where TMessage : notnull
      where TPerspective : class
      => throw new NotSupportedException("LocalInvokeAndSyncAsync with specific perspective requires a Dispatcher implementation with IPerspectiveSyncAwaiter support.");

  /// <summary>
  /// Invokes a void receptor and waits for a SPECIFIC perspective to process
  /// any events emitted during the invocation.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Unlike <see cref="LocalInvokeAndSyncAsync{TMessage}(TMessage,TimeSpan?,Action{Perspectives.Sync.SyncWaitingContext}?,Action{Perspectives.Sync.SyncDecisionContext}?,CancellationToken)"/>
  /// which waits for ALL perspectives, this method waits only for the specified perspective type.
  /// </para>
  /// <para>
  /// This method is named differently from the result-returning overload to avoid generic type
  /// parameter ambiguity between TMessage,TResult and TMessage,TPerspective.
  /// </para>
  /// </remarks>
  /// <typeparam name="TMessage">The message type.</typeparam>
  /// <typeparam name="TPerspective">The perspective type to wait for.</typeparam>
  /// <param name="message">The message to process.</param>
  /// <param name="timeout">Maximum time to wait for the perspective to sync. Defaults to 30 seconds.</param>
  /// <param name="onWaiting">Optional callback invoked when waiting begins.</param>
  /// <param name="onDecisionMade">Optional callback always invoked when the sync decision is made.</param>
  /// <param name="cancellationToken">A cancellation token.</param>
  /// <returns>A <see cref="Perspectives.Sync.SyncResult"/> indicating sync outcome.</returns>
  /// <docs>fundamentals/dispatcher/dispatcher#local-invoke-and-sync-perspective</docs>
  Task<Perspectives.Sync.SyncResult> LocalInvokeAndSyncForPerspectiveAsync<TMessage, TPerspective>(
      TMessage message,
      TimeSpan? timeout = null,
      Action<Perspectives.Sync.SyncWaitingContext>? onWaiting = null,
      Action<Perspectives.Sync.SyncDecisionContext>? onDecisionMade = null,
      CancellationToken cancellationToken = default)
      where TMessage : notnull
      where TPerspective : class
      => throw new NotSupportedException("LocalInvokeAndSyncForPerspectiveAsync requires a Dispatcher implementation with IPerspectiveSyncAwaiter support.");

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
  /// Sends multiple typed messages to local receptors ONLY (no outbox delivery).
  /// Messages are processed in-process via strongly-typed delegates (AOT-compatible).
  /// Throws <see cref="ReceptorNotFoundException"/> if any message has no local receptor.
  /// </summary>
  /// <typeparam name="TMessage">The message type</typeparam>
  /// <param name="messages">The messages to send locally</param>
  /// <returns>All delivery receipts (Delivered status)</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherOutboxTests.cs:LocalSendManyAsync_Generic_WithLocalReceptor_DoesNotPublishToOutboxAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherOutboxTests.cs:LocalSendManyAsync_Generic_ProcessesAllMessagesLocallyAsync</tests>
  /// <docs>fundamentals/dispatcher/dispatcher#localsendmanyasync</docs>
  ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull;

  /// <summary>
  /// Sends multiple messages to local receptors ONLY (no outbox delivery).
  /// For AOT compatibility, use the generic overload LocalSendManyAsync&lt;TMessage&gt;.
  /// Throws <see cref="ReceptorNotFoundException"/> if any message has no local receptor.
  /// </summary>
  /// <param name="messages">The messages to send locally</param>
  /// <returns>All delivery receipts (Delivered status)</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherOutboxTests.cs:LocalSendManyAsync_NonGeneric_WithLocalReceptor_DoesNotPublishToOutboxAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherOutboxTests.cs:LocalSendManyAsync_NonGeneric_ProcessesAllMessagesLocallyAsync</tests>
  /// <docs>fundamentals/dispatcher/dispatcher#localsendmanyasync</docs>
  ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages);

  /// <summary>
  /// Publishes multiple events with event routing (namespace-specific topics).
  /// Each event is processed locally (if handlers exist) and queued to the outbox.
  /// </summary>
  /// <typeparam name="TEvent">The event type</typeparam>
  /// <param name="events">The events to publish</param>
  /// <returns>All delivery receipts</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherOutboxTests.cs:PublishManyAsync_Generic_QueuesAllEventsWithEventRoutingAsync</tests>
  /// <docs>fundamentals/dispatcher/dispatcher#publishmanyasync</docs>
  Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull;

  /// <summary>
  /// Publishes multiple events. For AOT compatibility, use the generic overload.
  /// </summary>
  /// <param name="events">The events to publish</param>
  /// <returns>All delivery receipts</returns>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherOutboxTests.cs:PublishManyAsync_NonGeneric_QueuesAllEventsWithEventRoutingAsync</tests>
  /// <docs>fundamentals/dispatcher/dispatcher#publishmanyasync</docs>
  Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events);

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
