using Whizbang.Core;
using Whizbang.Core.Observability;

namespace ECommerce.Integration.Tests.Fixtures;

/// <summary>
/// Test service instance provider with fixed instance ID and service name.
/// </summary>
internal sealed class TestServiceInstanceProvider : IServiceInstanceProvider {
  public TestServiceInstanceProvider(Guid instanceId, string serviceName) {
    InstanceId = instanceId;
    ServiceName = serviceName;
  }

  public Guid InstanceId { get; }
  public string ServiceName { get; }
  public string HostName => "test-host";
  public int ProcessId => 12345;

  public ServiceInstanceInfo ToInfo() => new() {
    InstanceId = InstanceId,
    ServiceName = ServiceName,
    HostName = HostName,
    ProcessId = ProcessId
  };
}
