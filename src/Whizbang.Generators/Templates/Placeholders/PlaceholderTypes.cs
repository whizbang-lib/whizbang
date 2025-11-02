// Placeholder types used in generator templates for string replacement.
// These types are never compiled - they're just markers that get replaced
// with actual message/response types during code generation.

namespace Whizbang.Generators.Templates.Placeholders;

/// <summary>
/// Placeholder for message type in template snippets.
/// Gets replaced with actual message type during generation.
/// </summary>
public class __MESSAGE_TYPE__ { }

/// <summary>
/// Placeholder for response type in template snippets.
/// Gets replaced with actual response type during generation.
/// </summary>
public class __RESPONSE_TYPE__ { }

/// <summary>
/// Placeholder for IReceptor interface in template snippets.
/// Gets replaced with fully-qualified IReceptor interface name during generation. 
/// </summary>
public class __RECEPTOR_INTERFACE__<TMessage, TResponse> { }
