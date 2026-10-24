// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
    await Assert.That(provider.ServiceName).IsEqualTo("MyTestService");
    await Assert.That(provider.InstanceId).IsNotEqualTo(Guid.Empty);
    await Assert.That(provider.HostName).IsNotNull();
    await Assert.That(provider.ProcessId).IsGreaterThan(0);
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
    await Assert.That(provider.ServiceName).IsEqualTo("MyOtherService");
  }

  [Test]
  public async Task ServiceInstanceProvider_WithoutConfiguration_UsesAssemblyNameAsync() {
    // Arrange & Act
    var provider = new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build());

    // Assert
    // Service name should be entry assembly name (Whizbang.Core.Tests when running tests)
    await Assert.That(provider.ServiceName).IsNotNull();
    await Assert.That(provider.ServiceName).IsNotEqualTo(string.Empty);
    // Entry assembly name varies by test runner, so just verify it's set
    await Assert.That(provider.ServiceName.Length).IsGreaterThan(0);
  }

  [Test]
  public async Task ServiceInstanceProvider_WithExplicitValues_UsesProvidedValuesAsync() {
    // Arrange
    var instanceId = Guid.NewGuid();
    const string serviceName = "ExplicitService";
    const string hostName = "test-host";
    const int processId = 12345;

    // Act
    var provider = new ServiceInstanceProvider(instanceId, serviceName, hostName, processId);

    // Assert
    await Assert.That(provider.InstanceId).IsEqualTo(instanceId);
    await Assert.That(provider.ServiceName).IsEqualTo(serviceName);
    await Assert.That(provider.HostName).IsEqualTo(hostName);
    await Assert.That(provider.ProcessId).IsEqualTo(processId);
  }

  [Test]
  public async Task ServiceInstanceProvider_ToInfo_ReturnsServiceInstanceInfoAsync() {
    // Arrange
    var instanceId = Guid.NewGuid();
    const string serviceName = "TestService";
    const string hostName = "test-host";
    const int processId = 99999;
    var provider = new ServiceInstanceProvider(instanceId, serviceName, hostName, processId);

    // Act
    var info = provider.ToInfo();

    // Assert
    await Assert.That(info.InstanceId).IsEqualTo(instanceId);
    await Assert.That(info.ServiceName).IsEqualTo(serviceName);
    await Assert.That(info.HostName).IsEqualTo(hostName);
    await Assert.That(info.ProcessId).IsEqualTo(processId);
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
    await Assert.That(ReferenceEquals(info1, info2)).IsTrue()
      .Because("ToInfo() should return the same cached instance to avoid allocations");
  }

  [Test]
  public async Task ServiceInstanceProvider_GeneratesUUIDv7_ForInstanceIdAsync() {
    // Arrange & Act
    var provider = new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build());
    var instanceId = provider.InstanceId;

    // Assert
    await Assert.That(instanceId).IsNotEqualTo(Guid.Empty);

    // Verify UUIDv7 version bits (version 7 in bits 48-51)
    var bytes = instanceId.ToByteArray();
    var versionBits = (bytes[7] & 0xF0) >> 4;
    await Assert.That(versionBits).IsEqualTo(0x7)
      .Because("InstanceId should be UUIDv7 (version 7)");
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
    await Assert.That(provider.ServiceName).IsEqualTo("WhizbangService")
      .Because("Whizbang:ServiceName should take precedence over ServiceName");
  }

  [Test]
  public async Task ResolveServiceName_NoEntryAssembly_UnknownAsync() {
    var configuration = new ConfigurationBuilder().Build();

    await Assert.That(ServiceInstanceProvider.ResolveServiceName(configuration, null)).IsEqualTo("Unknown");
  }

  [Test]
  public async Task ResolveServiceName_NamelessEntry_UnknownAsync() {
    var configuration = new ConfigurationBuilder().Build();

    await Assert.That(ServiceInstanceProvider.ResolveServiceName(configuration, new NamelessAssembly())).IsEqualTo("Unknown");
  }

  /// <summary>
  /// <c>Whizbang:ServiceName</c> present but empty (an appsettings placeholder, or an environment variable set to
  /// nothing) names no service, so the next source is read, as when the key is absent.
  /// </summary>
  [Test]
  [Arguments("")]
  [Arguments("   ")]
  public async Task ResolveServiceName_BlankWhizbangServiceName_ReadsServiceNameAsync(string blank) {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["Whizbang:ServiceName"] = blank, ["ServiceName"] = "orders" })
      .Build();

    await Assert.That(ServiceInstanceProvider.ResolveServiceName(configuration, null)).IsEqualTo("orders");
  }

  /// <summary>Both keys blank: the entry assembly names the service, as when neither is set.</summary>
  [Test]
  [Arguments("")]
  [Arguments("   ")]
  public async Task ResolveServiceName_BothKeysBlank_UsesTheEntryAssemblyAsync(string blank) {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["Whizbang:ServiceName"] = blank, ["ServiceName"] = blank })
      .Build();
    var entry = typeof(ServiceInstanceProviderTests).Assembly;

    await Assert.That(ServiceInstanceProvider.ResolveServiceName(configuration, entry)).IsEqualTo(entry.GetName().Name);
  }

  private sealed class NamelessAssembly : System.Reflection.Assembly {
    public override System.Reflection.AssemblyName GetName() => new();
  }
}
