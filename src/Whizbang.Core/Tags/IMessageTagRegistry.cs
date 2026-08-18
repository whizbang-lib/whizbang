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
/// <docs>fundamentals/messages/message-tags#registry</docs>
/// <tests>Whizbang.Core.Tests/Tags/MessageTagProcessorTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/MessageTagProcessorTests.cs:ProcessTagsAsync_WithMatchingTag_InvokesHookAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/MessageTagProcessorTests.cs:ProcessTagsAsync_WithMultipleTags_ProcessesAllAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/TagHookStageFilteringAndScopeTests.cs:ProcessTagsAsync_TwoHooksAtDifferentStages_EachFiresOnlyAtItsStageAsync</tests>
public interface IMessageTagRegistry {
  /// <summary>
  /// Gets all tag registrations for a specific message type.
  /// </summary>
  /// <param name="messageType">The message type to look up.</param>
  /// <returns>Tag registrations for the message type, or empty if none found.</returns>
  IEnumerable<MessageTagRegistration> GetTagsFor(Type messageType);

  /// <summary>
  /// Enumerates every tag registration this registry holds. Startup validation of tag
  /// policies (reserved <c>sys-</c> prefix, coalesce-binding ambiguity) rides this surface —
  /// the per-type lookup cannot answer "which tags exist at all".
  /// </summary>
  /// <remarks>
  /// Default implementation returns empty so hand-written registries (test fakes, older
  /// generated code) keep compiling; generated registries override it with their full table.
  /// </remarks>
  /// <returns>All registrations, or empty when the registry predates enumeration.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Tags/MessageTagRegistryGetAllTagsTests.cs:GetAllTags_DefaultInterfaceMember_ReturnsEmptyAsync</tests>
  IEnumerable<MessageTagRegistration> GetAllTags() => [];
}
