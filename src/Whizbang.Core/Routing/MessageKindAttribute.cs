namespace Whizbang.Core.Routing;

/// <summary>
/// Explicitly specifies the message kind for a type, overriding interface and convention detection.
/// </summary>
/// <example>
/// <code>
/// [MessageKind(MessageKind.Command)]
/// public sealed record CreateOrderMessage : IMessage;
/// </code>
/// </example>
/// <docs>fundamentals/dispatcher/routing#message-kind</docs>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class MessageKindAttribute(MessageKind kind) : Attribute {
  /// <summary>
  /// Gets the message kind for this type.
  /// </summary>
  public MessageKind Kind { get; } = kind;
}
