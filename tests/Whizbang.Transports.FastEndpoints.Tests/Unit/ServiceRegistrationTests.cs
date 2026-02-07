using Microsoft.Extensions.DependencyInjection;
using Whizbang.Transports.FastEndpoints;

namespace Whizbang.Transports.FastEndpoints.Tests.Unit;

/// <summary>
/// Tests for FastEndpoints service registration extensions.
/// </summary>
public class ServiceRegistrationTests {
  [Test]
  public async Task AddWhizbangLenses_ShouldReturnSameServicesInstanceAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var result = services.AddWhizbangLenses();

    // Assert - fluent interface returns same instance
    await Assert.That(result).IsEqualTo(services);
  }

  [Test]
  public async Task AddWhizbangLenses_ShouldBeCallableMultipleTimesAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act - should not throw when called multiple times
    var result1 = services.AddWhizbangLenses();
    var result2 = services.AddWhizbangLenses();

    // Assert - both calls return the same services instance
    await Assert.That(result1).IsEqualTo(services);
    await Assert.That(result2).IsEqualTo(services);
  }

  [Test]
  public async Task AddWhizbangMutations_ShouldReturnSameServicesInstanceAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var result = services.AddWhizbangMutations();

    // Assert
    await Assert.That(result).IsEqualTo(services);
  }

  [Test]
  public async Task AddWhizbangMutations_ShouldBeCallableMultipleTimesAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act - should not throw when called multiple times
    var result1 = services.AddWhizbangMutations();
    var result2 = services.AddWhizbangMutations();

    // Assert - both calls return the same services instance
    await Assert.That(result1).IsEqualTo(services);
    await Assert.That(result2).IsEqualTo(services);
  }

  [Test]
  public async Task AddWhizbangLenses_AndMutations_CanBeChainedAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var result = services
        .AddWhizbangLenses()
        .AddWhizbangMutations();

    // Assert
    await Assert.That(result).IsEqualTo(services);
  }
}
