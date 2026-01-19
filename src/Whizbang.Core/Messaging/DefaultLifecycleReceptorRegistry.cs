using System.Collections.Concurrent;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Thread-safe implementation of <see cref="ILifecycleReceptorRegistry"/> using concurrent collections.
/// </summary>
/// <remarks>
/// <para>
/// This implementation is safe for concurrent registration and unregistration from multiple threads.
/// Primarily used in integration tests where receptors may be registered/unregistered while
/// the application is processing messages.
/// </para>
/// <para>
/// For AOT compatibility, receptors are converted to delegates at registration time using reflection.
/// This means reflection happens once during test setup, but invocation is reflection-free and AOT-safe.
/// </para>
/// </remarks>
/// <docs>testing/lifecycle-synchronization</docs>
public sealed class DefaultLifecycleReceptorRegistry : ILifecycleReceptorRegistry {
  // Key: (MessageType, LifecycleStage), Value: List of (receptor instance, invocation delegate) pairs
  private readonly ConcurrentDictionary<(Type MessageType, LifecycleStage Stage), List<(object Receptor, Func<object, ILifecycleContext?, CancellationToken, ValueTask> Handler)>> _receptors = new();

  /// <inheritdoc/>
  public void Register<TMessage>(object receptor, LifecycleStage stage) where TMessage : IMessage {
    ArgumentNullException.ThrowIfNull(receptor);

    var key = (typeof(TMessage), stage);

    // Create AOT-compatible delegate (no reflection, uses pattern matching)
    var handler = _createHandler<TMessage>(receptor);

    _receptors.AddOrUpdate(
      key,
      _ => new List<(object, Func<object, ILifecycleContext?, CancellationToken, ValueTask>)> { (receptor, handler) },
      (_, existingList) => {
        lock (existingList) {
          existingList.Add((receptor, handler));
          return existingList;
        }
      }
    );
  }

  /// <inheritdoc/>
  public bool Unregister<TMessage>(object receptor, LifecycleStage stage) where TMessage : IMessage {
    ArgumentNullException.ThrowIfNull(receptor);

    var key = (typeof(TMessage), stage);

    if (_receptors.TryGetValue(key, out var receptorList)) {
      lock (receptorList) {
        // Find and remove the tuple containing this receptor instance
        var index = receptorList.FindIndex(x => ReferenceEquals(x.Receptor, receptor));
        if (index >= 0) {
          receptorList.RemoveAt(index);
          return true;
        }
      }
    }

    return false;
  }

  /// <inheritdoc/>
  public IReadOnlyList<object> GetReceptors(Type messageType, LifecycleStage stage) {
    ArgumentNullException.ThrowIfNull(messageType);

    var key = (messageType, stage);

    if (_receptors.TryGetValue(key, out var receptorList)) {
      lock (receptorList) {
        // Return a copy of just the receptor instances
        return receptorList.Select(x => x.Receptor).ToList();
      }
    }

    return Array.Empty<object>();
  }

  /// <inheritdoc/>
  public IReadOnlyList<Func<object, ILifecycleContext?, CancellationToken, ValueTask>> GetHandlers(Type messageType, LifecycleStage stage) {
    ArgumentNullException.ThrowIfNull(messageType);

    var key = (messageType, stage);

    if (_receptors.TryGetValue(key, out var receptorList)) {
      lock (receptorList) {
        // Return a copy of just the handler delegates (AOT-safe!)
        return receptorList.Select(x => x.Handler).ToList();
      }
    }

    return Array.Empty<Func<object, ILifecycleContext?, CancellationToken, ValueTask>>();
  }

  /// <summary>
  /// Creates an AOT-compatible invocation delegate for a void receptor.
  /// Uses compile-time pattern matching - completely reflection-free.
  /// </summary>
  /// <typeparam name="TMessage">The message type the receptor handles.</typeparam>
  /// <param name="receptor">The receptor instance to create a delegate for.</param>
  /// <returns>A delegate that can invoke the receptor without reflection.</returns>
  /// <exception cref="ArgumentException">Thrown if receptor doesn't implement IReceptor&lt;TMessage&gt;.</exception>
  /// <remarks>
  /// <para>
  /// Lifecycle receptors are side effects that don't return responses. Only void receptors
  /// (IReceptor&lt;TMessage&gt;) are supported in the runtime registry.
  /// </para>
  /// <para>
  /// The runtime registry is primarily for integration test scenarios where receptors need to be
  /// registered dynamically after the application starts. Production receptors should use the
  /// <see cref="FireAtAttribute"/> for compile-time discovery and source-generated invocation.
  /// </para>
  /// <para>
  /// This method is completely AOT-compatible - it uses pattern matching (compile-time)
  /// rather than reflection (runtime), making it safe for Native AOT scenarios.
  /// </para>
  /// </remarks>
  private static Func<object, ILifecycleContext?, CancellationToken, ValueTask> _createHandler<TMessage>(object receptor)
    where TMessage : IMessage {

    // Pattern matching is compile-time, not reflection - fully AOT-compatible!
    if (receptor is not IReceptor<TMessage> voidReceptor) {
      throw new ArgumentException(
        $"Receptor must implement IReceptor<{typeof(TMessage).Name}>. " +
        "Lifecycle receptors with response types are not supported in the runtime registry. " +
        "Use [FireAt] attribute for compile-time receptor discovery instead.",
        nameof(receptor)
      );
    }

    // Return a delegate that invokes the receptor - zero reflection!
    // If receptor implements IAcceptsLifecycleContext, call SetLifecycleContext before HandleAsync
    return async (msg, context, ct) => {
      // If receptor accepts context and context is provided, set it
      if (receptor is IAcceptsLifecycleContext contextAware && context is not null) {
        contextAware.SetLifecycleContext(context);
      }

      await voidReceptor.HandleAsync((TMessage)msg, ct);
    };
  }
}
