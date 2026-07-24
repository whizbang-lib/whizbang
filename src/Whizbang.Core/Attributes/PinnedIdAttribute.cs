namespace Whizbang.Core.Attributes;

/// <summary>
/// Declares a stable, namespace-proof identifier for a concrete message or perspective type.
/// Pinned IDs decouple type identity from CLR namespaces so stored events can survive
/// namespace restructuring. Discovered by PinnedIdRegistryGenerator for AOT-safe lookup.
/// </summary>
/// <remarks>
/// Apply to concrete IMessage (events + commands) and IPerspectiveFor&lt;&gt; implementors.
/// The analyzer (WHIZ100 / WHIZ101 warning; WHIZ102 error for invalid GUID) enforces
/// presence and format. The attribute itself accepts any non-empty string.
/// </remarks>
/// <example>
/// <code>
/// [PinnedId("a1b2c3d4-e5f6-7890-abcd-1234567890ab")]
/// public sealed record OrderPlacedEvent(Guid OrderId) : IEvent;
/// </code>
/// </example>
/// <docs>core-concepts/pinned-identity</docs>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class PinnedIdAttribute(string id) : Attribute {
  /// <summary>
  /// Gets the pinned ID. Expected to be a GUID string; the analyzer validates the format.
  /// </summary>
  public string Id { get; } = id switch {
    null => throw new ArgumentNullException(nameof(id)),
    _ when string.IsNullOrWhiteSpace(id) => throw new ArgumentException("Pinned ID cannot be empty or whitespace.", nameof(id)),
    _ => id
  };
}
