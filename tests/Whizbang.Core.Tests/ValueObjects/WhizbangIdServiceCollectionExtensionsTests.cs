using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// Tests for WhizbangIdServiceCollectionExtensions - DI registration for WhizbangId factories.
/// Validates factory registration and provider configuration.
/// </summary>
[Category("Core")]
[Category("ValueObjects")]
[Category("DependencyInjection")]
public class WhizbangIdServiceCollectionExtensionsTests {

  [Test]
  public async Task AddWhizbangIdFactory_WithValidFactory_ShouldRegisterFactoryAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbangIdFactory<ITestId, TestIdFactory>();

    // Assert
    var descriptor = services.FirstOrDefault(s => s.ServiceType == typeof(IWhizbangIdFactory<ITestId>));
    await Assert.That(descriptor).IsNotNull();
    await Assert.That(descriptor!.ImplementationType).IsEqualTo(typeof(TestIdFactory));
    await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
  }

  [Test]
  public async Task AddWhizbangIdFactory_ShouldReturnServiceCollectionAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var result = services.AddWhizbangIdFactory<ITestId, TestIdFactory>();

    // Assert
    await Assert.That(result).IsEqualTo(services);
  }

  [Test]
  public async Task AddWhizbangIdFactory_RegisteredFactory_CanBeResolvedAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangIdFactory<ITestId, TestIdFactory>();

    // Act
    var serviceProvider = services.BuildServiceProvider();
    var factory = serviceProvider.GetService<IWhizbangIdFactory<ITestId>>();

    // Assert
    await Assert.That(factory).IsNotNull();
    await Assert.That(factory).IsTypeOf<TestIdFactory>();

    // Verify factory works
    var id = factory!.Create();
    await Assert.That(id).IsNotNull();
    await Assert.That(id).IsTypeOf<TestId>();
  }

  [Test]
  public async Task AddWhizbangIdFactory_MultipleFactories_ShouldRegisterAllAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangIdFactory<ITestId, TestIdFactory>();
    services.AddWhizbangIdFactory<ISecondTestId, SecondTestIdFactory>();

    // Act
    var serviceProvider = services.BuildServiceProvider();
    var factory1 = serviceProvider.GetService<IWhizbangIdFactory<ITestId>>();
    var factory2 = serviceProvider.GetService<IWhizbangIdFactory<ISecondTestId>>();

    // Assert
    await Assert.That(factory1).IsNotNull();
    await Assert.That(factory1).IsTypeOf<TestIdFactory>();
    await Assert.That(factory2).IsNotNull();
    await Assert.That(factory2).IsTypeOf<SecondTestIdFactory>();
  }

  [Test]
  [NotInParallel("WhizbangIdProvider")]  // Shared static state - must run sequentially
  public async Task ConfigureWhizbangIdProvider_WithValidProvider_ShouldSetGlobalProviderAsync() {
    // Arrange
    var services = new ServiceCollection();
    var expectedGuid = Guid.Parse("12345678-9abc-def0-1234-567890abcdef");
    var testProvider = new TestIdProvider(expectedGuid);

    try {
      // Act
      services.ConfigureWhizbangIdProvider(testProvider);
      var result = WhizbangIdProvider.NewGuid();

      // Assert
      await Assert.That(result).IsEqualTo(expectedGuid);
    } finally {
      // Restore default provider
      WhizbangIdProvider.SetProvider(new Uuid7IdProvider());
    }
  }

  [Test]
  [NotInParallel("WhizbangIdProvider")]  // Shared static state - must run sequentially
  public async Task ConfigureWhizbangIdProvider_ShouldReturnServiceCollectionAsync() {
    // Arrange
    var services = new ServiceCollection();
    var testProvider = new TestIdProvider(Guid.NewGuid());

    try {
      // Act
      var result = services.ConfigureWhizbangIdProvider(testProvider);

      // Assert
      await Assert.That(result).IsEqualTo(services);
    } finally {
      // Restore default provider
      WhizbangIdProvider.SetProvider(new Uuid7IdProvider());
    }
  }

  [Test]
  [NotInParallel("WhizbangIdProvider")]  // Shared static state - must run sequentially
  public async Task ConfigureWhizbangIdProvider_WithCustomProvider_ShouldAffectGlobalGenerationAsync() {
    // Arrange
    var services = new ServiceCollection();
    var expectedGuid = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210");
    var customProvider = new TestIdProvider(expectedGuid);

    try {
      // Act
      services.ConfigureWhizbangIdProvider(customProvider);

      // Generate IDs and verify they use custom provider
      var id1 = WhizbangIdProvider.NewGuid();
      var id2 = WhizbangIdProvider.NewGuid();

      // Assert
      await Assert.That(id1).IsEqualTo(expectedGuid);
      await Assert.That(id2).IsEqualTo(expectedGuid);
    } finally {
      // Restore default provider
      WhizbangIdProvider.SetProvider(new Uuid7IdProvider());
    }
  }

  [Test]
  public async Task AddWhizbangIdFactory_FactoryLifetime_ShouldBeSingletonAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangIdFactory<ITestId, TestIdFactory>();

    // Act
    var serviceProvider = services.BuildServiceProvider();
    var factory1 = serviceProvider.GetService<IWhizbangIdFactory<ITestId>>();
    var factory2 = serviceProvider.GetService<IWhizbangIdFactory<ITestId>>();

    // Assert
    await Assert.That(factory1).IsNotNull();
    await Assert.That(factory2).IsNotNull();
    await Assert.That(ReferenceEquals(factory1, factory2)).IsTrue();
  }

  // Test factory for testing
  private interface ITestId { }
  private class TestId : ITestId { }
  private class TestIdFactory : IWhizbangIdFactory<ITestId> {
    public ITestId Create() => new TestId();
  }

  // Second test factory for multiple factory registration test
  private interface ISecondTestId { }
  private class SecondTestId : ISecondTestId { }
  private class SecondTestIdFactory : IWhizbangIdFactory<ISecondTestId> {
    public ISecondTestId Create() => new SecondTestId();
  }

  [Test]
  public async Task AddWhizbangIdProviders_RegistersAllProvidersAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbangIdProviders();
    var provider = services.BuildServiceProvider();

    // Assert - Check that typed providers are registered
    var registryTestId1Provider = provider.GetService<IWhizbangIdProvider<RegistryTestId1>>();
    var registryTestId2Provider = provider.GetService<IWhizbangIdProvider<RegistryTestId2>>();

    await Assert.That(registryTestId1Provider).IsNotNull();
    await Assert.That(registryTestId2Provider).IsNotNull();

    // Verify providers work
    var id1 = registryTestId1Provider!.NewId();
    var id2 = registryTestId2Provider!.NewId();

    await Assert.That(id1.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(id2.Value).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  [NotInParallel("WhizbangIdProvider")]  // Shared static state
  public async Task AddWhizbangIdProviders_WithCustomProvider_UsesCustomProviderAsync() {
    // Arrange
    var services = new ServiceCollection();
    var expectedGuid = Guid.NewGuid();
    var customProvider = new TestIdProvider(expectedGuid);

    try {
      // Act
      services.AddWhizbangIdProviders(customProvider);
      var provider = services.BuildServiceProvider();
      var typedProvider = provider.GetRequiredService<IWhizbangIdProvider<RegistryTestId1>>();

      var id = typedProvider.NewId();

      // Assert
      await Assert.That(id.Value).IsEqualTo(expectedGuid);
    } finally {
      // Restore default
      WhizbangIdProvider.SetProvider(new Uuid7IdProvider());
    }
  }

  [Test]
  public async Task AddWhizbangIdProviders_RegistersBaseProviderAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbangIdProviders();
    var provider = services.BuildServiceProvider();

    // Assert
    var baseProvider = provider.GetService<IWhizbangIdProvider>();
    await Assert.That(baseProvider).IsNotNull();
    await Assert.That(baseProvider).IsTypeOf<Uuid7IdProvider>();
  }

  [Test]
  public async Task TypedProvider_InjectedInService_CreatesValidIdsAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangIdProviders();
    services.AddSingleton<TestService>();

    // Act
    var provider = services.BuildServiceProvider();
    var testService = provider.GetRequiredService<TestService>();
    var id = testService.CreateId();

    // Assert
    await Assert.That(id.Value).IsNotEqualTo(Guid.Empty);
  }

  // Test service that uses typed provider
  private class TestService {
    private readonly IWhizbangIdProvider<RegistryTestId1> _provider;

    public TestService(IWhizbangIdProvider<RegistryTestId1> provider) {
      _provider = provider;
    }

    public RegistryTestId1 CreateId() => _provider.NewId();
  }

  // Custom test provider for testing
  private class TestIdProvider : IWhizbangIdProvider {
    private readonly Guid _fixedGuid;

    public TestIdProvider(Guid fixedGuid) {
      _fixedGuid = fixedGuid;
    }

    public Guid NewGuid() => _fixedGuid;
  }
}
