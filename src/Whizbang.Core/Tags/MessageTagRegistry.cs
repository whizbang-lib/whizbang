using Whizbang.Core.Registry;

namespace Whizbang.Core.Tags;

/// <summary>
/// Registry for message tag contributions from multiple assemblies.
/// Each assembly registers its generated tag registry via [ModuleInitializer].
/// </summary>
/// <remarks>
/// <para>
/// This is a convenience wrapper around <see cref="AssemblyRegistry{T}"/> for <see cref="IMessageTagRegistry"/>
/// that queries all registered registries to find tags for a message type.
/// </para>
/// <para>
/// <strong>How it works:</strong>
/// <list type="number">
/// <item>Assembly loads → [ModuleInitializer] runs → registers its IMessageTagRegistry (priority 100)</item>
/// <item>MessageTagProcessor.ProcessTagsAsync() → calls GetTagsFor() → queries all registries</item>
/// <item>First registry with matching tags returns them</item>
/// </list>
/// </para>
/// </remarks>
/// <docs>core-concepts/message-tags#registry</docs>
/// <tests>Whizbang.Core.Tests/Tags/MessageTagRegistryTests.cs</tests>
public static class MessageTagRegistry {
  /// <summary>
  /// Register a tag registry. Called from [ModuleInitializer] in generated code.
  /// </summary>
  /// <param name="registry">The registry to register</param>
  /// <param name="priority">Lower = tried first. Use 100 for contracts, 1000 for services.</param>
  public static void Register(IMessageTagRegistry registry, int priority = 1000) {
    AssemblyRegistry<IMessageTagRegistry>.Register(registry, priority);
  }

  /// <summary>
  /// Get all tags for a message type by querying all registered registries.
  /// Returns tags from all registries that have matching entries.
  /// </summary>
  /// <param name="messageType">The message type to look up.</param>
  /// <returns>All tag registrations for the message type across all registries.</returns>
  public static IEnumerable<MessageTagRegistration> GetTagsFor(Type messageType) {
    foreach (var registry in AssemblyRegistry<IMessageTagRegistry>.GetOrderedContributions()) {
      foreach (var tag in registry.GetTagsFor(messageType)) {
        yield return tag;
      }
    }
  }

  /// <summary>
  /// Count of registered tag registries (for diagnostics/testing).
  /// </summary>
  public static int Count => AssemblyRegistry<IMessageTagRegistry>.Count;
}
