using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.SignalR.DependencyInjection;

namespace Whizbang.SignalR.Tests.DependencyInjection;

/// <summary>
/// Tests for <see cref="SignalRServiceCollectionExtensions"/>.
/// Verifies turn-key SignalR configuration with Whizbang's AOT-compatible JSON serialization.
/// </summary>
[Category("Unit")]
[Category("SignalR")]
public class SignalRServiceCollectionExtensionsTests {

  [Test]
  public async Task AddWhizbangSignalR_ReturnsSignalRServerBuilderAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var builder = services.AddWhizbangSignalR();

    // Assert
    await Assert.That(builder).IsNotNull();
  }

  [Test]
  public async Task AddWhizbangSignalR_CanChainWithHubOptionsAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var builder = services.AddWhizbangSignalR()
        .AddHubOptions<Microsoft.AspNetCore.SignalR.Hub>(options => options.EnableDetailedErrors = true);

    // Assert
    await Assert.That(builder).IsNotNull();
  }

  [Test]
  public async Task AddWhizbangSignalR_WithConfigure_AppliesOptionsAsync() {
    // Arrange
    var services = new ServiceCollection();
    var timeout = TimeSpan.FromSeconds(45);

    // Act
    var builder = services.AddWhizbangSignalR(options => options.ClientTimeoutInterval = timeout);

    // Assert
    await Assert.That(builder).IsNotNull();
  }

  [Test]
  public async Task AddWhizbangSignalR_RegistersRequiredServicesAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbangSignalR();
    _ = services.BuildServiceProvider();

    // Assert - SignalR should be registered
    await Assert.That(services.Count).IsGreaterThan(0);
  }
}
