using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core;

/// <summary>
/// Delegate for invoking a receptor's ReceiveAsync method.
/// Generated code creates these delegates with proper type safety - zero reflection.
/// </summary>
public delegate ValueTask<TResult> ReceptorInvoker<TResult>(object message);

/// <summary>
/// Delegate for invoking a void receptor's ReceiveAsync method without returning a result.
/// Generated code creates these delegates with proper type safety - zero reflection.
/// Enables zero-allocation pattern for command/event handling.
/// </summary>
public delegate ValueTask VoidReceptorInvoker(object message);

/// <summary>
/// Delegate for invoking multiple receptors for publish operations.
/// Generated code creates these delegates with proper type safety - zero reflection.
/// </summary>
public delegate Task ReceptorPublisher<in TEvent>(TEvent @event);

/// <summary>
/// Base dispatcher class with core logic. The source generator creates a derived class
/// that implements the abstract lookup methods, returning strongly-typed delegates.
/// This achieves zero-reflection while keeping functional logic in the base class.
/// </summary>
public abstract class Dispatcher : IDispatcher {
  private readonly IServiceProvider _internalServiceProvider;
  private readonly ITraceStore? _traceStore;

  protected Dispatcher(IServiceProvider serviceProvider, ITraceStore? traceStore = null) {
    _internalServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    _traceStore = traceStore;
  }

  /// <summary>
  /// Gets the service provider for receptor resolution.
  /// Available to generated derived class.
  /// </summary>
  protected IServiceProvider _serviceProvider => _internalServiceProvider;

  // ========================================
  // SEND PATTERN - Command Dispatch with Acknowledgment
  // ========================================

  /// <summary>
  /// Sends a message and returns a delivery receipt (not the business result).
  /// Creates a new message context automatically.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  public Task<IDeliveryReceipt> SendAsync(object message) {
    var context = MessageContext.New();
    return SendAsync(message, context);
  }

  /// <summary>
  /// Sends a message with an explicit context and returns a delivery receipt.
  /// Uses generated delegate to invoke receptor with zero reflection.
  /// Creates MessageEnvelope with hop for observability.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  public async Task<IDeliveryReceipt> SendAsync(
    object message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) {
    ArgumentNullException.ThrowIfNull(message);

    if (context == null) {
      throw new ArgumentNullException(nameof(context));
    }

    var messageType = message.GetType();

    // Get strongly-typed delegate from generated code
    // We need to use object as TResult since we don't know the actual result type
    var invoker = _getReceptorInvoker<object>(message, messageType);

    if (invoker == null) {
      throw new HandlerNotFoundException(messageType);
    }

    // Create envelope with hop for observability
    var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);

    // Store envelope if trace store is configured
    if (_traceStore != null) {
      await _traceStore.StoreAsync(envelope);
    }

    // Invoke using delegate - zero reflection, strongly typed
    await invoker(message);

    // Return delivery receipt
    var destination = messageType.Name; // Will be enhanced with actual receptor name in future
    return DeliveryReceipt.Delivered(
      envelope.MessageId,
      destination,
      context.CorrelationId,
      context.CausationId
    );
  }

  // ========================================
  // LOCAL INVOKE PATTERN - In-Process RPC
  // ========================================

  /// <summary>
  /// Invokes a receptor in-process and returns the typed business result.
  /// Creates a new message context automatically.
  /// PERFORMANCE: Zero allocation when trace store is null, target &lt; 20ns per invocation.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) {
    var context = MessageContext.New();
    return LocalInvokeAsync<TResult>(message, context);
  }

  /// <summary>
  /// Invokes a receptor in-process with explicit context and returns the typed business result.
  /// Uses generated delegate to invoke receptor with zero reflection.
  /// Skips envelope creation when trace store is null for optimal performance.
  /// PERFORMANCE: Zero allocation fast path for synchronously-completed receptors (no async/await overhead).
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  public ValueTask<TResult> LocalInvokeAsync<TResult>(
    object message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) {
    ArgumentNullException.ThrowIfNull(message);

    if (context == null) {
      throw new ArgumentNullException(nameof(context));
    }

    var messageType = message.GetType();

    // Get strongly-typed delegate from generated code
    var invoker = _getReceptorInvoker<TResult>(message, messageType);

    if (invoker == null) {
      throw new HandlerNotFoundException(messageType);
    }

    // OPTIMIZATION: Skip envelope creation when trace store is null
    // This achieves zero allocation for high-throughput scenarios
    if (_traceStore != null) {
      return _localInvokeWithTracingAsync(message, context, invoker, callerMemberName, callerFilePath, callerLineNumber);
    }

    // FAST PATH: Zero allocation when no tracing
    // Invoke using delegate - zero reflection, strongly typed
    // Avoid async/await state machine allocation by returning task directly
    return invoker(message);
  }

  /// <summary>
  /// Slow path for LocalInvoke when tracing is enabled.
  /// Uses async/await to store envelope before invoking receptor.
  /// </summary>
  private async ValueTask<TResult> _localInvokeWithTracingAsync<TResult>(
    object message,
    IMessageContext context,
    ReceptorInvoker<TResult> invoker,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);
    await _traceStore!.StoreAsync(envelope);

    // Invoke using delegate - zero reflection, strongly typed
    var result = await invoker(message);
    return result;
  }

  // ========================================
  // VOID LOCAL INVOKE PATTERN - Zero Allocation Command/Event Handling
  // ========================================

  /// <summary>
  /// Invokes a void receptor in-process without returning a business result.
  /// Creates a new message context automatically.
  /// PERFORMANCE: Zero allocation target for command/event patterns.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  public ValueTask LocalInvokeAsync(object message) {
    var context = MessageContext.New();
    return LocalInvokeAsync(message, context);
  }

  /// <summary>
  /// Invokes a void receptor in-process with explicit context without returning a business result.
  /// Uses generated delegate to invoke receptor with zero reflection.
  /// Skips envelope creation when trace store is null for optimal performance.
  /// PERFORMANCE: Zero allocation fast path for synchronously-completed receptors.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  public ValueTask LocalInvokeAsync(
    object message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) {
    ArgumentNullException.ThrowIfNull(message);

    if (context == null) {
      throw new ArgumentNullException(nameof(context));
    }

    var messageType = message.GetType();

    // Get strongly-typed void delegate from generated code
    var invoker = _getVoidReceptorInvoker(message, messageType);

    if (invoker == null) {
      throw new HandlerNotFoundException(messageType);
    }

    // OPTIMIZATION: Skip envelope creation when trace store is null
    // This achieves zero allocation for high-throughput scenarios
    if (_traceStore != null) {
      return _localInvokeVoidWithTracingAsync(message, context, invoker, callerMemberName, callerFilePath, callerLineNumber);
    }

    // FAST PATH: Zero allocation when no tracing
    // Invoke using delegate - zero reflection, strongly typed
    // Avoid async/await state machine allocation by returning task directly
    return invoker(message);
  }

  /// <summary>
  /// Slow path for void LocalInvoke when tracing is enabled.
  /// Uses async/await to store envelope before invoking receptor.
  /// </summary>
  private async ValueTask _localInvokeVoidWithTracingAsync(
    object message,
    IMessageContext context,
    VoidReceptorInvoker invoker,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);
    await _traceStore!.StoreAsync(envelope);

    // Invoke using delegate - zero reflection, strongly typed
    await invoker(message);
  }

  /// <summary>
  /// Creates a MessageEnvelope with initial hop containing caller information and context.
  /// </summary>
  private IMessageEnvelope _createEnvelope<TMessage>(
    TMessage message,
    IMessageContext context,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    var envelope = new MessageEnvelope<TMessage> {
      MessageId = MessageId.New(),
      Payload = message!,
      Hops = new List<MessageHop>()
    };

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "Unknown",
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = context.CorrelationId,
      CausationId = context.CausationId,
      CallerMemberName = callerMemberName,
      CallerFilePath = callerFilePath,
      CallerLineNumber = callerLineNumber
    };

    envelope.AddHop(hop);
    return envelope;
  }

  /// <summary>
  /// Publishes an event to all registered handlers.
  /// Uses generated delegate to invoke receptors with zero reflection.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  [RequiresUnreferencedCode("Event types and handlers are resolved using runtime type information. For AOT compatibility, ensure all event types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Event publishing uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  public async Task PublishAsync<TEvent>(TEvent @event) {
    if (@event == null) {
      throw new ArgumentNullException(nameof(@event));
    }

    var eventType = @event.GetType();

    // Get strongly-typed delegate from generated code
    var publisher = _getReceptorPublisher(@event, eventType);

    // Invoke using delegate - zero reflection, strongly typed
    await publisher(@event);
  }

  // ========================================
  // BATCH OPERATIONS
  // ========================================

  /// <summary>
  /// Sends multiple messages and collects all delivery receipts.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  public async Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) {
    ArgumentNullException.ThrowIfNull(messages);

    var receipts = new List<IDeliveryReceipt>();
    foreach (var message in messages) {
      var receipt = await SendAsync(message);
      receipts.Add(receipt);
    }
    return receipts;
  }

  /// <summary>
  /// Invokes multiple receptors in-process and collects all typed business results.
  /// PERFORMANCE: Zero allocation when trace store is null, target &lt; 20ns per invocation.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  [RequiresUnreferencedCode("Message types and handlers are resolved using runtime type information. For AOT compatibility, ensure all message types and handlers are registered at compile time.")]
  [RequiresDynamicCode("Message dispatching uses generic type parameters that may require runtime code generation. For AOT compatibility, use source-generated dispatcher.")]
  public async ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) {
    ArgumentNullException.ThrowIfNull(messages);

    var results = new List<TResult>();
    foreach (var message in messages) {
      var result = await LocalInvokeAsync<TResult>(message);
      results.Add(result);
    }
    return results;
  }

  /// <summary>
  /// Implemented by generated code - returns a strongly-typed delegate for invoking a receptor.
  /// The delegate encapsulates the receptor lookup and invocation with zero reflection.
  /// </summary>
  [UnconditionalSuppressMessage("AOT", "IL2026", Justification = "Generated code uses compile-time type resolution with no reflection.")]
  [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Generated code uses compile-time type resolution with no dynamic code generation.")]
  protected abstract ReceptorInvoker<TResult>? _getReceptorInvoker<TResult>(object message, Type messageType);

  /// <summary>
  /// Implemented by generated code - returns a strongly-typed delegate for invoking a void receptor.
  /// The delegate encapsulates the receptor lookup and invocation with zero reflection.
  /// Returns null if no void receptor is registered for the message type.
  /// </summary>
  [UnconditionalSuppressMessage("AOT", "IL2026", Justification = "Generated code uses compile-time type resolution with no reflection.")]
  [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Generated code uses compile-time type resolution with no dynamic code generation.")]
  protected abstract VoidReceptorInvoker? _getVoidReceptorInvoker(object message, Type messageType);

  /// <summary>
  /// Implemented by generated code - returns a strongly-typed delegate for publishing to receptors.
  /// The delegate encapsulates finding all receptors and invoking them with zero reflection.
  /// </summary>
  [UnconditionalSuppressMessage("AOT", "IL2026", Justification = "Generated code uses compile-time type resolution with no reflection.")]
  [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Generated code uses compile-time type resolution with no dynamic code generation.")]
  protected abstract ReceptorPublisher<TEvent> _getReceptorPublisher<TEvent>(TEvent @event, Type eventType);
}
