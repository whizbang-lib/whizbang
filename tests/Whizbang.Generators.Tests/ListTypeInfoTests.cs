namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for ListTypeInfo - ensures value equality for incremental generator caching.
/// </summary>
public class ListTypeInfoTests {

  [Test]
  public async Task ListTypeInfo_ValueEquality_ComparesFieldsAsync() {
    // Arrange - Create two instances with same values
    var info1 = new ListTypeInfo(
      "global::System.Collections.Generic.List<global::MyApp.OrderLineItem>",
      "global::MyApp.OrderLineItem",
      "OrderLineItem"
    );
    var info2 = new ListTypeInfo(
      "global::System.Collections.Generic.List<global::MyApp.OrderLineItem>",
      "global::MyApp.OrderLineItem",
      "OrderLineItem"
    );

    // Act & Assert - Records use value equality
    await Assert.That(info1).IsEqualTo(info2);
    await Assert.That(info1.GetHashCode()).IsEqualTo(info2.GetHashCode());
  }

  [Test]
  public async Task ListTypeInfo_Constructor_SetsPropertiesAsync() {
    // Arrange & Act
    var info = new ListTypeInfo(
      "global::System.Collections.Generic.List<global::MyApp.Product>",
      "global::MyApp.Product",
      "Product"
    );

    // Assert
    await Assert.That(info.ListTypeName).IsEqualTo("global::System.Collections.Generic.List<global::MyApp.Product>");
    await Assert.That(info.ElementTypeName).IsEqualTo("global::MyApp.Product");
    await Assert.That(info.ElementSimpleName).IsEqualTo("Product");
  }
}
