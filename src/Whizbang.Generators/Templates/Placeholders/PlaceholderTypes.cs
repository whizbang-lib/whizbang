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
