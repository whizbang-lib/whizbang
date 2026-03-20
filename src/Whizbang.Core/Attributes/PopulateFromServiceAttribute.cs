#pragma warning disable S3604 // Primary constructor field/property initializers are intentional

namespace Whizbang.Core.Attributes;

/// <summary>
/// Marks a message property for automatic population from service instance information.
/// The property will be set with values from ServiceInstanceInfo (ServiceName, InstanceId, etc.).
/// </summary>
/// <remarks>
/// <para>
/// Apply this attribute to properties on message types (commands, events)
/// to automatically capture service instance information for observability.
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
/// public record OrderProcessed(
///   [property: StreamId] Guid OrderId,
///   string Status,
///   [property: PopulateFromService(ServiceKind.ServiceName)] string? ProcessedBy = null,
///   [property: PopulateFromService(ServiceKind.InstanceId)] Guid? InstanceId = null,
///   [property: PopulateFromService(ServiceKind.HostName)] string? HostName = null
/// ) : IEvent;
/// </code>
/// </remarks>
/// <docs>extending/attributes/auto-populate</docs>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/PopulateFromServiceAttributeTests.cs</tests>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class PopulateFromServiceAttribute(ServiceKind kind) : Attribute {
  /// <summary>
  /// Gets the kind of service value to populate.
  /// </summary>
  public ServiceKind Kind { get; } = kind;
}
