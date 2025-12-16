using Whizbang.Core;

namespace Whizbang.Core.Tests.ValueObjects;

// Test ID types for this file
[WhizbangId]
public readonly partial struct GenericTestId1;

[WhizbangId]
public readonly partial struct GenericTestId2;

public class IWhizbangIdProviderGenericTests {
  [Test]
  public async Task NewId_WithUuid7Provider_ReturnsValidIdAsync() {
    // Arrange
    var baseProvider = new Uuid7IdProvider();
    var provider = GenericTestId1.CreateProvider(baseProvider);

    // Act
    var id = provider.NewId();

    // Assert
    await Assert.That(id.Value).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task NewId_WithCustomProvider_UsesCustomProviderAsync() {
    // Arrange
    var expectedGuid = Guid.NewGuid();
    var customProvider = new TestWhizbangIdProvider(expectedGuid);
    var provider = GenericTestId1.CreateProvider(customProvider);

    // Act
    var id = provider.NewId();

    // Assert
    await Assert.That(id.Value).IsEqualTo(expectedGuid);
  }

  [Test]
  public async Task NewId_GeneratesUniqueIdsAsync() {
    // Arrange
    var baseProvider = new Uuid7IdProvider();
    var provider = GenericTestId2.CreateProvider(baseProvider);

    // Act
    var id1 = provider.NewId();
    var id2 = provider.NewId();

    // Assert
    await Assert.That(id1).IsNotEqualTo(id2);
  }

  [Test]
  public async Task Provider_WithNullBaseProvider_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    IWhizbangIdProvider? nullProvider = null;

    // Act & Assert
    await Assert.That(() => GenericTestId1.CreateProvider(nullProvider!))
      .Throws<ArgumentNullException>();
  }

  // Test helper class
  private class TestWhizbangIdProvider : IWhizbangIdProvider {
    private readonly Guid _guid;

    public TestWhizbangIdProvider(Guid guid) {
      _guid = guid;
    }

    public Guid NewGuid() => _guid;
  }
}
