namespace Whizbang.Core.Attributes;

/// <summary>
/// Marks a message property for automatic population from the current security context.
/// The property will be set with values from SecurityContext (UserId, TenantId).
/// </summary>
/// <remarks>
/// <para>
/// Apply this attribute to string properties on message types (commands, events)
/// to automatically capture security context information without manual code.
/// </para>
/// <para>
/// Values are stored in the MessageEnvelope metadata to preserve message immutability.
/// Access populated values via envelope extension methods or use Materialize&lt;T&gt;()
/// to create a new message instance with populated values.
/// </para>
/// <para>
/// <strong>Example:</strong>
/// </para>
/// <code>
/// public record DocumentCreated(
///   [property: StreamId] Guid DocumentId,
///   string Title,
///   [property: PopulateFromContext(ContextKind.UserId)] string? CreatedBy = null,
///   [property: PopulateFromContext(ContextKind.TenantId)] string? TenantId = null
/// ) : IEvent;
/// </code>
/// </remarks>
/// <docs>attributes/auto-populate</docs>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/PopulateFromContextAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class PopulateFromContextAttribute(ContextKind kind) : Attribute {
  /// <summary>
  /// Gets the kind of context value to populate.
  /// </summary>
  public ContextKind Kind { get; } = kind;
}
