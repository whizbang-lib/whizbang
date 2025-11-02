using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Whizbang.Core;

/// <summary>
/// Delegate for invoking a receptor's ReceiveAsync method.
/// Generated code creates these delegates with proper type safety - zero reflection.
/// </summary>
public delegate Task<TResult> ReceptorInvoker<TResult>(object message);

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
  private readonly IServiceProvider _serviceProvider;

  protected Dispatcher(IServiceProvider serviceProvider) {
    _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
  }

  /// <summary>
  /// Gets the service provider for receptor resolution.
  /// Available to generated derived class.
  /// </summary>
  protected IServiceProvider ServiceProvider => _serviceProvider;

  /// <summary>
  /// Sends a message and returns a typed result.
  /// Creates a new message context automatically.
  /// </summary>
  [DebuggerStepThrough]
  public Task<TResult> SendAsync<TResult>(object message) {
    var context = MessageContext.New();
    return SendAsync<TResult>(message, context);
  }

  /// <summary>
  /// Sends a message with an explicit context and returns a typed result.
  /// Uses generated delegate to invoke receptor with zero reflection.
  /// </summary>
  [DebuggerStepThrough]
  public async Task<TResult> SendAsync<TResult>(object message, IMessageContext context) {
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

    // Invoke using delegate - zero reflection, strongly typed
    var result = await invoker(message);
    return result;
  }

  /// <summary>
  /// Publishes an event to all registered handlers.
  /// Uses generated delegate to invoke receptors with zero reflection.
  /// </summary>
  [DebuggerStepThrough]
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

  /// <summary>
  /// Sends multiple messages and returns all results.
  /// </summary>
  [DebuggerStepThrough]
  public async Task<IEnumerable<TResult>> SendManyAsync<TResult>(IEnumerable<object> messages) {
    ArgumentNullException.ThrowIfNull(messages);

    var results = new List<TResult>();
    foreach (var message in messages) {
      var result = await SendAsync<TResult>(message);
      results.Add(result);
    }
    return results;
  }

  /// <summary>
  /// Implemented by generated code - returns a strongly-typed delegate for invoking a receptor.
  /// The delegate encapsulates the receptor lookup and invocation with zero reflection.
  /// </summary>
  protected abstract ReceptorInvoker<TResult>? _getReceptorInvoker<TResult>(object message, Type messageType);

  /// <summary>
  /// Implemented by generated code - returns a strongly-typed delegate for publishing to receptors.
  /// The delegate encapsulates finding all receptors and invoking them with zero reflection.
  /// </summary>
  protected abstract ReceptorPublisher<TEvent> _getReceptorPublisher<TEvent>(TEvent @event, Type eventType);
}
