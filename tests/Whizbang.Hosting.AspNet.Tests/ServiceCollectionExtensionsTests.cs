using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Hosting.AspNet;

namespace Whizbang.Hosting.AspNet.Tests;

/// <summary>
/// Tests for AddWhizbangAspNet service registration.
/// </summary>
public class ServiceCollectionExtensionsTests {

  [Test]
  public async Task AddWhizbangAspNet_RegistersStartupFilterAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbangAspNet();

    // Assert
    var descriptor = services.FirstOrDefault(d =>
      d.ServiceType == typeof(IStartupFilter) &&
      d.ImplementationType == typeof(WhizbangFlushStartupFilter));

    await Assert.That(descriptor).IsNotNull()
      .Because("AddWhizbangAspNet should register the startup filter");
    await Assert.That(descriptor!.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
  }

  [Test]
  public async Task AddWhizbangAspNet_CalledMultipleTimes_RegistersOnceAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act — call multiple times
    services.AddWhizbangAspNet();
    services.AddWhizbangAspNet();
    services.AddWhizbangAspNet();

    // Assert — only one registration (TryAddEnumerable prevents duplicates)
    var count = services.Count(d =>
      d.ServiceType == typeof(IStartupFilter) &&
      d.ImplementationType == typeof(WhizbangFlushStartupFilter));

    await Assert.That(count).IsEqualTo(1)
      .Because("TryAddEnumerable should prevent duplicate startup filter registrations");
  }
}
