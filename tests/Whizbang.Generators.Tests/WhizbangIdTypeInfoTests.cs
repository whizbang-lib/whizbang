namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for WhizbangIdTypeInfo - ensures value equality for incremental generator caching.
/// </summary>
public class WhizbangIdTypeInfoTests {

  [Test]
  public async Task WhizbangIdTypeInfo_ValueEquality_ComparesFieldsAsync() {
    // Arrange - Create two instances with same values
    var info1 = new WhizbangIdInfo("ProductId", "MyApp.Domain", DiscoverySource.ExplicitType, false);
    var info2 = new WhizbangIdInfo("ProductId", "MyApp.Domain", DiscoverySource.ExplicitType, false);

    // Act & Assert - Records use value equality
    await Assert.That(info1).IsEqualTo(info2);
    await Assert.That(info1.GetHashCode()).IsEqualTo(info2.GetHashCode());
  }

  [Test]
  public async Task WhizbangIdTypeInfo_Constructor_SetsPropertiesAsync() {
    // Arrange & Act
    var info = new WhizbangIdInfo("ProductId", "MyApp.Domain", DiscoverySource.Property, true);

    // Assert
    await Assert.That(info.TypeName).IsEqualTo("ProductId");
    await Assert.That(info.Namespace).IsEqualTo("MyApp.Domain");
    await Assert.That(info.Source).IsEqualTo(DiscoverySource.Property);
    await Assert.That(info.SuppressDuplicateWarning).IsTrue();
    await Assert.That(info.FullyQualifiedName).IsEqualTo("MyApp.Domain.ProductId");
  }
}
