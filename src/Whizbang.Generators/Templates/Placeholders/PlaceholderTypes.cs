// Placeholder types used in generator templates for string replacement.
// These types are never compiled - they're just markers that get replaced
// with actual message/response types during code generation.

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Whizbang.Generators.Templates.Placeholders;

/// <summary>
/// Placeholder for message type in template snippets.
/// Gets replaced with actual message type during generation.
/// </summary>
[CompilerGenerated]
[EditorBrowsable(EditorBrowsableState.Never)]
public class __MESSAGE_TYPE__ { }

/// <summary>
/// Placeholder for response type in template snippets.
/// Gets replaced with actual response type during generation.
/// </summary>
[CompilerGenerated]
[EditorBrowsable(EditorBrowsableState.Never)]
public class __RESPONSE_TYPE__ { }

/// <summary>
/// Placeholder for IReceptor interface in template snippets.
/// Gets replaced with fully-qualified IReceptor interface name during generation.
/// </summary>
[CompilerGenerated]
[EditorBrowsable(EditorBrowsableState.Never)]
public class __RECEPTOR_INTERFACE__<TMessage, TResponse> { }

/// <summary>
/// Placeholder for IReceptor interface with only message (void return) in template snippets.
/// Gets replaced with fully-qualified IReceptor interface name during generation.
/// </summary>
[CompilerGenerated]
[EditorBrowsable(EditorBrowsableState.Never)]
public class __RECEPTOR_INTERFACE__<TMessage> { }

/// <summary>
/// Placeholder for ISyncReceptor interface in template snippets.
/// Gets replaced with fully-qualified ISyncReceptor interface name during generation.
/// </summary>
[CompilerGenerated]
[EditorBrowsable(EditorBrowsableState.Never)]
public class __SYNC_RECEPTOR_INTERFACE__<TMessage, TResponse> {
  public TResponse Handle(TMessage message) => default!;
}

/// <summary>
/// Placeholder for ISyncReceptor interface with only message (void return) in template snippets.
/// Gets replaced with fully-qualified ISyncReceptor interface name during generation.
/// </summary>
[CompilerGenerated]
[EditorBrowsable(EditorBrowsableState.Never)]
public class __SYNC_RECEPTOR_INTERFACE__<TMessage> {
  public void Handle(TMessage message) { }
}

/// <summary>
/// Placeholder for receptor class in template snippets.
/// Gets replaced with actual receptor class name during generation.
/// </summary>
[CompilerGenerated]
[EditorBrowsable(EditorBrowsableState.Never)]
public class __RECEPTOR_CLASS__ { }

/// <summary>
/// Placeholder for perspective interface in template snippets.
/// Gets replaced with fully-qualified IPerspectiveFor interface name during generation.
/// </summary>
[CompilerGenerated]
[EditorBrowsable(EditorBrowsableState.Never)]
public class __PERSPECTIVE_INTERFACE__<TEvent> { }

/// <summary>
/// Placeholder for perspective class in template snippets.
/// Gets replaced with actual perspective class name during generation.
/// </summary>
[CompilerGenerated]
[EditorBrowsable(EditorBrowsableState.Never)]
public class __PERSPECTIVE_CLASS__ { }

/// <summary>
/// Placeholder for event type in template snippets.
/// Gets replaced with actual event type during generation.
/// </summary>
[CompilerGenerated]
[EditorBrowsable(EditorBrowsableState.Never)]
public class __EVENT_TYPE__ { }

// Delegate placeholders for template snippets (mirrors Whizbang.Core.Dispatcher delegates)

/// <summary>
/// Placeholder delegate for async receptor invocation.
/// </summary>
public delegate System.Threading.Tasks.ValueTask<TResult> ReceptorInvoker<TResult>(object message);

/// <summary>
/// Placeholder delegate for void async receptor invocation.
/// </summary>
public delegate System.Threading.Tasks.ValueTask VoidReceptorInvoker(object message);

/// <summary>
/// Placeholder delegate for sync receptor invocation.
/// </summary>
public delegate TResult SyncReceptorInvoker<out TResult>(object message);

/// <summary>
/// Placeholder delegate for void sync receptor invocation.
/// </summary>
public delegate void VoidSyncReceptorInvoker(object message);

/// <summary>
/// Placeholder delegate for event publishing.
/// </summary>
public delegate System.Threading.Tasks.Task ReceptorPublisher<TEvent>(TEvent @event);

/// <summary>
/// Placeholder for lifecycle stage enum.
/// </summary>
public enum LifecycleStage { ImmediateAsync, PostPerspectiveAsync }

/// <summary>
/// Placeholder for cancellation token.
/// </summary>
[CompilerGenerated]
[EditorBrowsable(EditorBrowsableState.Never)]
public struct CancellationToken { }
