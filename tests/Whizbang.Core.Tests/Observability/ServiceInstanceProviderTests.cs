using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Tests for ServiceInstanceProvider.
/// Verifies service instance information generation and configuration resolution.
/// </summary>
[Category("Observability")]
[Category("ServiceInstance")]
public class ServiceInstanceProviderTests {

  [Test]
  public async Task ServiceInstanceProvider_WithConfiguration_ResolvesServiceName_FromWhizbangKeyAsync() {
    // Arrange
    var configBuilder = new ConfigurationBuilder();
    configBuilder.AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:ServiceName"] = "MyTestService"
    });
    var configuration = configBuilder.Build();

    // Act
    var provider = new ServiceInstanceProvider(configuration);

    // Assert
    // TODO: Verify ServiceName == "MyTestService"
    // TODO: Verify InstanceId is UUIDv7 (not empty)
    // TODO: Verify HostName is set
    // TODO: Verify ProcessId > 0
    throw new NotImplementedException("Test needs implementation - track test gaps with grep 'NotImplementedException'");
  }

  [Test]
  public async Task ServiceInstanceProvider_WithConfiguration_ResolvesServiceName_FromServiceNameKeyAsync() {
    // Arrange
    var configBuilder = new ConfigurationBuilder();
    configBuilder.AddInMemoryCollection(new Dictionary<string, string?> {
      ["ServiceName"] = "MyOtherService"
    });
    var configuration = configBuilder.Build();

    // Act
    var provider = new ServiceInstanceProvider(configuration);

    // Assert
    // TODO: Verify ServiceName == "MyOtherService"
    // TODO: Verify Whizbang:ServiceName takes precedence if both exist
    throw new NotImplementedException("Test needs implementation - track test gaps with grep 'NotImplementedException'");
  }

  [Test]
  public async Task ServiceInstanceProvider_WithoutConfiguration_UsesAssemblyNameAsync() {
    // Arrange & Act
    var provider = new ServiceInstanceProvider(configuration: null);

    // Assert
    // TODO: Verify ServiceName is assembly name (e.g., "Whizbang.Core.Tests")
    // TODO: OR verify ServiceName == "Unknown" if no assembly
    throw new NotImplementedException("Test needs implementation - track test gaps with grep 'NotImplementedException'");
  }

  [Test]
  public async Task ServiceInstanceProvider_WithExplicitValues_UsesProvidedValuesAsync() {
    // Arrange
    var instanceId = Guid.NewGuid();
    var serviceName = "ExplicitService";
    var hostName = "test-host";
    var processId = 12345;

    // Act
    var provider = new ServiceInstanceProvider(instanceId, serviceName, hostName, processId);

    // Assert
    // TODO: Verify InstanceId == instanceId
    // TODO: Verify ServiceName == serviceName
    // TODO: Verify HostName == hostName
    // TODO: Verify ProcessId == processId
    throw new NotImplementedException("Test needs implementation - track test gaps with grep 'NotImplementedException'");
  }

  [Test]
  public async Task ServiceInstanceProvider_ToInfo_ReturnsServiceInstanceInfoAsync() {
    // Arrange
    var instanceId = Guid.NewGuid();
    var serviceName = "TestService";
    var hostName = "test-host";
    var processId = 99999;
    var provider = new ServiceInstanceProvider(instanceId, serviceName, hostName, processId);

    // Act
    var info = provider.ToInfo();

    // Assert
    // TODO: Verify info.InstanceId == instanceId
    // TODO: Verify info.ServiceName == serviceName
    // TODO: Verify info.HostName == hostName
    // TODO: Verify info.ProcessId == processId
    throw new NotImplementedException("Test needs implementation - track test gaps with grep 'NotImplementedException'");
  }

  [Test]
  public async Task ServiceInstanceProvider_ToInfo_CachesResultAsync() {
    // Arrange
    var provider = new ServiceInstanceProvider(
      Guid.NewGuid(),
      "TestService",
      "test-host",
      12345
    );

    // Act
    var info1 = provider.ToInfo();
    var info2 = provider.ToInfo();

    // Assert
    // TODO: Verify info1 and info2 are the same instance (ReferenceEquals)
    // TODO: Verify caching avoids object allocation on subsequent calls
    throw new NotImplementedException("Test needs implementation - track test gaps with grep 'NotImplementedException'");
  }

  [Test]
  public async Task ServiceInstanceProvider_GeneratesUUIDv7_ForInstanceIdAsync() {
    // Arrange & Act
    var provider = new ServiceInstanceProvider(configuration: null);
    var instanceId = provider.InstanceId;

    // Assert
    // TODO: Verify instanceId is not empty
    // TODO: Verify instanceId is UUIDv7 (version 7)
    // TODO: Extract timestamp from UUIDv7 and verify it's recent
    throw new NotImplementedException("Test needs implementation - track test gaps with grep 'NotImplementedException'");
  }

  [Test]
  public async Task ServiceInstanceProvider_ConfigurationPrecedence_WhizbangKeyOverridesServiceNameKeyAsync() {
    // Arrange
    var configBuilder = new ConfigurationBuilder();
    configBuilder.AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:ServiceName"] = "WhizbangService",
      ["ServiceName"] = "GenericService"
    });
    var configuration = configBuilder.Build();

    // Act
    var provider = new ServiceInstanceProvider(configuration);

    // Assert
    // TODO: Verify ServiceName == "WhizbangService" (Whizbang:ServiceName takes precedence)
    throw new NotImplementedException("Test needs implementation - track test gaps with grep 'NotImplementedException'");
  }
}
