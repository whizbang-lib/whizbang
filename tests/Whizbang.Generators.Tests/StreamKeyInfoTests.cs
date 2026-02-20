namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for StreamKeyInfo - ensures value equality for incremental generator caching.
/// </summary>
public class StreamKeyInfoTests {

  [Test]
  public async Task StreamKeyInfo_ValueEquality_ComparesFieldsAsync() {
    // Arrange - Create two instances with same values
    var info1 = new StreamKeyInfo(
      "global::MyApp.Events.OrderCreated",
      "OrderId",
      "global::System.Guid",
      IsPropertyValueType: true
    );
    var info2 = new StreamKeyInfo(
      "global::MyApp.Events.OrderCreated",
      "OrderId",
      "global::System.Guid",
      IsPropertyValueType: true
    );

    // Act & Assert - Records use value equality
    await Assert.That(info1).IsEqualTo(info2);
    await Assert.That(info1.GetHashCode()).IsEqualTo(info2.GetHashCode());
  }

  [Test]
  public async Task StreamKeyInfo_Constructor_SetsPropertiesAsync() {
    // Arrange & Act
    var info = new StreamKeyInfo(
      "global::MyApp.Events.ProductUpdated",
      "ProductId",
      "global::MyApp.Domain.ProductId",
      IsPropertyValueType: true
    );

    // Assert
    await Assert.That(info.EventType).IsEqualTo("global::MyApp.Events.ProductUpdated");
    await Assert.That(info.PropertyName).IsEqualTo("ProductId");
    await Assert.That(info.PropertyType).IsEqualTo("global::MyApp.Domain.ProductId");
    await Assert.That(info.IsPropertyValueType).IsTrue();
  }
}
