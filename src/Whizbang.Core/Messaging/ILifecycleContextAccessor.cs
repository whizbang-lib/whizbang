namespace Whizbang.Core.Messaging;

/// <summary>
/// Provides access to the current lifecycle context during receptor invocation.
/// Used by runtime-registered receptors (via <see cref="IAcceptsLifecycleContext"/>) to access
/// lifecycle metadata (stage, stream ID, perspective type, etc.) during invocation.
/// </summary>
/// <remarks>
/// <para>
/// This accessor is backed by AsyncLocal and set by <see cref="IReceptorInvoker"/> before
/// each receptor invocation. Runtime-registered receptor delegates read from it to
/// support <see cref="IAcceptsLifecycleContext"/>.
/// </para>
/// </remarks>
/// <docs>operations/testing/lifecycle-synchronization</docs>
public interface ILifecycleContextAccessor {
  /// <summary>
  /// Gets or sets the current lifecycle context for the ambient scope.
  /// </summary>
  ILifecycleContext? Current { get; set; }
}

/// <summary>
/// Default implementation of <see cref="ILifecycleContextAccessor"/> using AsyncLocal.
/// </summary>
internal sealed class AsyncLocalLifecycleContextAccessor : ILifecycleContextAccessor {
  private static readonly AsyncLocal<ILifecycleContext?> _current = new();

  /// <inheritdoc/>
  public ILifecycleContext? Current {
    get => _current.Value;
    set => _current.Value = value;
  }
}
