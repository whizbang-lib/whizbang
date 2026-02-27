namespace Whizbang.Core.Tags;

/// <summary>
/// Provides access to message tag registrations for a compilation.
/// </summary>
/// <remarks>
/// <para>
/// The tag registry is populated by the MessageTagDiscoveryGenerator at compile time.
/// It provides AOT-compatible tag discovery without reflection.
/// </para>
/// <para>
/// For testing, implementations can be created manually to provide
/// tag registrations without requiring generated code.
/// </para>
/// </remarks>
/// <docs>core-concepts/message-tags#registry</docs>
/// <tests>Whizbang.Core.Tests/Tags/MessageTagProcessorTests.cs</tests>
public interface IMessageTagRegistry {
  /// <summary>
  /// Gets all tag registrations for a specific message type.
  /// </summary>
  /// <param name="messageType">The message type to look up.</param>
  /// <returns>Tag registrations for the message type, or empty if none found.</returns>
  IEnumerable<MessageTagRegistration> GetTagsFor(Type messageType);
}
