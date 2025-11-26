using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for WhizbangBuilder - the unified entry point for Whizbang configuration.
/// Target: 100% branch coverage.
/// </summary>
public class WhizbangBuilderTests {
  [Test]
  public async Task Constructor_WithValidServices_InitializesSuccessfullyAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var builder = new WhizbangBuilder(services);

    // Assert
    await Assert.That(builder.Services).IsSameReferenceAs(services);
  }

  [Test]
  public async Task Constructor_WithNullServices_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    ServiceCollection? services = null;

    // Act & Assert
    var exception = await Assert.That(() => new WhizbangBuilder(services!))
        .Throws<ArgumentNullException>();

    await Assert.That(exception.ParamName).IsEqualTo("services");
  }

  [Test]
  public async Task Services_ReturnsInjectedServiceCollectionAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangBuilder(services);

    // Act
    var result = builder.Services;

    // Assert
    await Assert.That(result).IsSameReferenceAs(services);
  }
}
