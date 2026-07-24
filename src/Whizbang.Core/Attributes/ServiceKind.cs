namespace Whizbang.Core.Attributes;

/// <summary>
/// Specifies the kind of service instance value to auto-populate on a message property.
/// Values are sourced from ServiceInstanceInfo during message dispatch.
/// </summary>
/// <remarks>
/// <para>
/// Use with <see cref="PopulateFromServiceAttribute"/> to automatically capture
/// service instance information for observability and distributed tracing.
/// </para>
/// <para>
/// <list type="bullet">
/// <item><description><see cref="ServiceName"/> - The name of the service processing the message</description></item>
/// <item><description><see cref="InstanceId"/> - Unique identifier for the service instance</description></item>
/// <item><description><see cref="HostName"/> - The host/machine name where the service runs</description></item>
/// <item><description><see cref="ProcessId"/> - The operating system process ID</description></item>
/// </list>
/// </para>
/// </remarks>
/// <docs>extending/attributes/auto-populate</docs>
/// <tests>tests/Whizbang.Core.Tests/AutoPopulate/PopulateFromServiceAttributeTests.cs</tests>
public enum ServiceKind {
  /// <summary>
  /// Populated with the service name from ServiceInstanceInfo.ServiceName.
  /// Identifies which service is processing the message (e.g., "OrderService").
  /// </summary>
  ServiceName = 0,

  /// <summary>
  /// Populated with the unique instance identifier from ServiceInstanceInfo.InstanceId.
  /// Useful for identifying specific service instances in scaled deployments.
  /// </summary>
  InstanceId = 1,

  /// <summary>
  /// Populated with the host/machine name from ServiceInstanceInfo.HostName.
  /// Useful for debugging and identifying where messages are processed.
  /// </summary>
  HostName = 2,

  /// <summary>
  /// Populated with the process ID from ServiceInstanceInfo.ProcessId.
  /// The operating system process ID for low-level debugging.
  /// </summary>
  ProcessId = 3
}
