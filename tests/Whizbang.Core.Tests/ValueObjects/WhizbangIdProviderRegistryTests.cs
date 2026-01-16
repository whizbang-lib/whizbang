using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;

namespace Whizbang.Core.Tests.ValueObjects;

// Test ID types are defined in WhizbangIdTestTypes.cs
// This ensures source generator runs before test code references Provider types

public class WhizbangIdProviderRegistryTests {
  [Test]
  public async Task RegisterFactory_WithValidFactory_RegistersSuccessfullyAsync() {
    // Arrange
    var baseProvider = new Uuid7IdProvider();
    var factoryCalled = false;

    // Act
    WhizbangIdProviderRegistry.RegisterFactory<RegistryTestId1>(bp => {
      factoryCalled = true;
      // Use the static CreateProvider method instead of directly instantiating Provider
      return RegistryTestId1.CreateProvider(bp);
    });

    var provider = WhizbangIdProviderRegistry.CreateProvider<RegistryTestId1>(baseProvider);
    var id = provider.NewId();

    // Assert
    await Assert.That(factoryCalled).IsTrue();
    await Assert.That(id.Value).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task CreateProvider_WithRegisteredType_ReturnsProviderAsync() {
    // Arrange
    var baseProvider = new Uuid7IdProvider();
    WhizbangIdProviderRegistry.RegisterFactory<RegistryTestId2>(bp => RegistryTestId2.CreateProvider(bp));

    // Act
    var provider = WhizbangIdProviderRegistry.CreateProvider<RegistryTestId2>(baseProvider);
    var id = provider.NewId();

    // Assert
    await Assert.That(provider).IsNotNull();
    await Assert.That(id.Value).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task CreateProvider_WithNullBaseProvider_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    IWhizbangIdProvider? nullProvider = null;

    // Act & Assert
    await Assert.That(() => WhizbangIdProviderRegistry.CreateProvider<RegistryTestId1>(nullProvider!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task GetRegisteredIdTypes_ReturnsAllRegisteredTypesAsync() {
    // Act
    var types = WhizbangIdProviderRegistry.GetRegisteredIdTypes();

    // Assert
    await Assert.That(types).IsNotNull();
    await Assert.That(types).Contains(typeof(RegistryTestId1));
    await Assert.That(types).Contains(typeof(RegistryTestId2));
  }

  [Test]
  public async Task RegisterDICallback_WithValidCallback_RegistersSuccessfullyAsync() {
    // Arrange
    var callbackInvoked = false;
    WhizbangIdProviderRegistry.RegisterDICallback((services, provider) => {
      callbackInvoked = true;
    });

    // Act
    var services = new ServiceCollection();
    var baseProvider = new Uuid7IdProvider();
    WhizbangIdProviderRegistry.RegisterAllWithDI(services, baseProvider);

    // Assert
    await Assert.That(callbackInvoked).IsTrue();
  }

  [Test]
  public async Task RegisterAllWithDI_CallsAllRegisteredCallbacksAsync() {
    // Arrange
    var callbackCount = 0;
    WhizbangIdProviderRegistry.RegisterDICallback((s, p) => callbackCount++);
    WhizbangIdProviderRegistry.RegisterDICallback((s, p) => callbackCount++);

    // Act
    var services = new ServiceCollection();
    var baseProvider = new Uuid7IdProvider();
    WhizbangIdProviderRegistry.RegisterAllWithDI(services, baseProvider);

    // Assert
    await Assert.That(callbackCount).IsGreaterThanOrEqualTo(2);
  }
}
