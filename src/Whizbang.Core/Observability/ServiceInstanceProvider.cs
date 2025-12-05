using System.Reflection;

namespace Whizbang.Core.Observability;

/// <summary>
/// Default implementation of IServiceInstanceProvider.
/// Generates a unique UUIDv7 instance ID at construction time and captures
/// service name, host name, and process ID from the environment.
/// Register as a singleton to ensure consistent instance identity throughout the application lifetime.
/// </summary>
public sealed class ServiceInstanceProvider : IServiceInstanceProvider {
  /// <inheritdoc />
  public Guid InstanceId { get; }

  /// <inheritdoc />
  public string ServiceName { get; }

  /// <inheritdoc />
  public string HostName { get; }

  /// <inheritdoc />
  public int ProcessId { get; }

  /// <summary>
  /// Initializes a new instance with generated UUIDv7 instance ID and
  /// automatically captured service information from the environment.
  /// </summary>
  public ServiceInstanceProvider() {
    InstanceId = WhizbangIdProvider.NewGuid();
    ServiceName = Assembly.GetEntryAssembly()?.GetName().Name ?? "Unknown";
    HostName = Environment.MachineName;
    ProcessId = Environment.ProcessId;
  }

  /// <summary>
  /// Initializes a new instance with specific values.
  /// Used for testing or when instance information needs to be externally managed.
  /// </summary>
  /// <param name="instanceId">The instance ID to use</param>
  /// <param name="serviceName">The service name</param>
  /// <param name="hostName">The host machine name</param>
  /// <param name="processId">The process ID</param>
  public ServiceInstanceProvider(Guid instanceId, string serviceName, string hostName, int processId) {
    InstanceId = instanceId;
    ServiceName = serviceName;
    HostName = hostName;
    ProcessId = processId;
  }
}
