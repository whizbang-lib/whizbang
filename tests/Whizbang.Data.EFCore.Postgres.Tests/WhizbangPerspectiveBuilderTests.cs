using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for WhizbangPerspectiveBuilder.
/// Verifies constructor validation and Services property access.
/// Target: 100% branch coverage.
/// </summary>
public class WhizbangPerspectiveBuilderTests {
  [Test]
  public async Task Constructor_WithValidServices_InitializesSuccessfullyAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var builder = new WhizbangPerspectiveBuilder(services);

    // Assert
    await Assert.That(builder.Services).IsSameReferenceAs(services);
  }

  [Test]
  public async Task Constructor_WithNullServices_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    ServiceCollection? services = null;

    // Act & Assert
    var exception = await Assert.That(() => new WhizbangPerspectiveBuilder(services!))
        .Throws<ArgumentNullException>();

    await Assert.That(exception.ParamName).IsEqualTo("services");
  }

  [Test]
  public async Task Services_ReturnsInjectedServiceCollectionAsync() {
    // Arrange
    var services = new ServiceCollection();
    var builder = new WhizbangPerspectiveBuilder(services);

    // Act
    var result = builder.Services;

    // Assert
    await Assert.That(result).IsSameReferenceAs(services);
  }
}
