using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace Whizbang.Core.Observability;

/// <summary>
/// Default implementation of IServiceInstanceProvider.
/// Generates a unique UUIDv7 instance ID at construction time and captures
/// service name, host name, and process ID from the environment.
/// Service name can be configured via "Whizbang:ServiceName" or "ServiceName" configuration keys,
/// falling back to the entry assembly name.
/// Register as a singleton to ensure consistent instance identity throughout the application lifetime.
/// </summary>
public sealed class ServiceInstanceProvider : IServiceInstanceProvider {
  private ServiceInstanceInfo? _cachedInfo;

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
  /// Service name is resolved in this order:
  /// 1. Configuration key "Whizbang:ServiceName"
  /// 2. Configuration key "ServiceName"
  /// 3. Entry assembly name (without file extension)
  /// 4. "Unknown" (fallback)
  /// </summary>
  /// <param name="configuration">Optional configuration for resolving service name</param>
  public ServiceInstanceProvider(IConfiguration? configuration = null) {
    InstanceId = WhizbangIdProvider.NewGuid();

    // Resolve ServiceName from configuration or assembly
    ServiceName = configuration?["Whizbang:ServiceName"]
                  ?? configuration?["ServiceName"]
                  ?? Assembly.GetEntryAssembly()?.GetName().Name
                  ?? "Unknown";

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

  /// <inheritdoc />
  public ServiceInstanceInfo ToInfo() {
    // Lazily initialize and cache the ServiceInstanceInfo object
    // This avoids recreating the same immutable object on every call
    return _cachedInfo ??= new ServiceInstanceInfo {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }
}
