namespace Whizbang.Core.Attributes;

/// <summary>
/// Overrides the framework's message payload limit for one message type.
/// </summary>
/// <remarks>
/// Raise it for a type that is legitimately large, or lower it for one that must stay small. Read at
/// compile time into the message type catalog, so no reflection is involved at run time. A dispatch can
/// still override it for one call with <c>DispatchOptions.WithMaxPayloadBytes</c>.
/// </remarks>
/// <param name="bytes">The limit for this type's serialized payload, in bytes. Zero or less turns the limit off for the type.</param>
/// <docs>fundamentals/messages/payload-size-limit</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/MessagePayloadLimitsTests.cs</tests>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true, AllowMultiple = false)]
public sealed class MaxPayloadSizeAttribute(long bytes) : Attribute {
  /// <summary>The limit, in bytes; zero or less means no limit for the type.</summary>
  public long Bytes { get; } = bytes;
}
