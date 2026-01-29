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
/// <docs>core-concepts/routing#message-kind</docs>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class MessageKindAttribute : Attribute {
  /// <summary>
  /// Gets the message kind for this type.
  /// </summary>
  public MessageKind Kind { get; }

  /// <summary>
  /// Creates a new message kind attribute.
  /// </summary>
  /// <param name="kind">The message kind to assign to this type.</param>
  public MessageKindAttribute(MessageKind kind) {
    Kind = kind;
  }
}
