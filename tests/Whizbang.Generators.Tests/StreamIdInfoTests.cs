namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for <see cref="StreamIdInfo"/> record.
/// </summary>
public class StreamIdInfoTests {
  [Test]
  public async Task StreamIdInfo_Constructor_SetsPropertiesAsync() {
    // Arrange & Act
    var info = new StreamIdInfo(
      EventType: "global::MyApp.Events.OrderCreatedEvent",
      PropertyName: "OrderId",
      PropertyType: "global::System.Guid",
      IsPropertyValueType: true
    );

    // Assert
    await Assert.That(info.EventType).IsEqualTo("global::MyApp.Events.OrderCreatedEvent");
    await Assert.That(info.PropertyName).IsEqualTo("OrderId");
    await Assert.That(info.PropertyType).IsEqualTo("global::System.Guid");
    await Assert.That(info.IsPropertyValueType).IsTrue();
  }

  [Test]
  public async Task StreamIdInfo_ValueEquality_ComparesFieldsAsync() {
    // Arrange
    var info1 = new StreamIdInfo(
      "global::MyApp.Events.OrderCreatedEvent", "OrderId", "global::System.Guid", true
    );
    var info2 = new StreamIdInfo(
      "global::MyApp.Events.OrderCreatedEvent", "OrderId", "global::System.Guid", true
    );
    var info3 = new StreamIdInfo(
      "global::MyApp.Events.ProductCreatedEvent", "ProductId", "global::System.Guid", true
    );

    // Assert
    await Assert.That(info1).IsEqualTo(info2);
    await Assert.That(info1).IsNotEqualTo(info3);
    await Assert.That(info1.GetHashCode()).IsEqualTo(info2.GetHashCode());
  }

  [Test]
  public async Task StreamIdInfo_ValueEquality_DifferentPropertyName_NotEqualAsync() {
    // Arrange
    var info1 = new StreamIdInfo(
      "global::MyApp.Events.OrderEvent", "OrderId", "global::System.Guid", true
    );
    var info2 = new StreamIdInfo(
      "global::MyApp.Events.OrderEvent", "CustomerId", "global::System.Guid", true
    );

    // Assert
    await Assert.That(info1).IsNotEqualTo(info2);
  }

  [Test]
  public async Task StreamIdInfo_ValueEquality_DifferentIsValueType_NotEqualAsync() {
    // Arrange
    var info1 = new StreamIdInfo(
      "global::MyApp.Events.OrderEvent", "Id", "global::System.String", false
    );
    var info2 = new StreamIdInfo(
      "global::MyApp.Events.OrderEvent", "Id", "global::System.String", true
    );

    // Assert
    await Assert.That(info1).IsNotEqualTo(info2);
  }

  [Test]
  public async Task StreamIdInfo_Deconstruction_WorksCorrectlyAsync() {
    // Arrange
    var info = new StreamIdInfo(
      "global::MyApp.Events.ProductCreatedEvent",
      "ProductId",
      "global::MyApp.ProductId",
      true
    );

    // Act
    var (eventType, propertyName, propertyType, isPropertyValueType, hasGenerate, onlyIfEmpty) = info;

    // Assert
    await Assert.That(eventType).IsEqualTo("global::MyApp.Events.ProductCreatedEvent");
    await Assert.That(propertyName).IsEqualTo("ProductId");
    await Assert.That(propertyType).IsEqualTo("global::MyApp.ProductId");
    await Assert.That(isPropertyValueType).IsTrue();
    await Assert.That(hasGenerate).IsFalse();
    await Assert.That(onlyIfEmpty).IsFalse();
  }

  [Test]
  public async Task StreamIdInfo_StringPropertyType_IsNotValueTypeAsync() {
    // Arrange & Act
    var info = new StreamIdInfo(
      EventType: "global::MyApp.Events.UserCreatedEvent",
      PropertyName: "UserId",
      PropertyType: "global::System.String",
      IsPropertyValueType: false
    );

    // Assert
    await Assert.That(info.IsPropertyValueType).IsFalse();
  }

  [Test]
  public async Task StreamIdInfo_CustomIdValueType_IsValueTypeAsync() {
    // Arrange & Act
    var info = new StreamIdInfo(
      EventType: "global::MyApp.Events.OrderCreatedEvent",
      PropertyName: "OrderId",
      PropertyType: "global::MyApp.OrderId",
      IsPropertyValueType: true
    );

    // Assert
    await Assert.That(info.IsPropertyValueType).IsTrue();
    await Assert.That(info.PropertyType).IsEqualTo("global::MyApp.OrderId");
  }

  [Test]
  public async Task StreamIdInfo_HashCode_ConsistentForEqualObjectsAsync() {
    // Arrange
    var info1 = new StreamIdInfo(
      "global::MyApp.Events.TestEvent", "TestId", "global::System.Guid", true
    );
    var info2 = new StreamIdInfo(
      "global::MyApp.Events.TestEvent", "TestId", "global::System.Guid", true
    );

    // Act
    var hash1 = info1.GetHashCode();
    var hash2 = info2.GetHashCode();

    // Assert
    await Assert.That(hash1).IsEqualTo(hash2);
  }
}

/// <summary>
/// Tests for <see cref="CommandStreamIdInfo"/> record.
/// </summary>
public class CommandStreamIdInfoTests {
  [Test]
  public async Task CommandStreamIdInfo_Constructor_SetsPropertiesAsync() {
    // Arrange & Act
    var info = new CommandStreamIdInfo(
      CommandType: "global::MyApp.Commands.CreateOrderCommand",
      PropertyName: "OrderId",
      PropertyType: "global::System.Guid",
      IsPropertyValueType: true
    );

    // Assert
    await Assert.That(info.CommandType).IsEqualTo("global::MyApp.Commands.CreateOrderCommand");
    await Assert.That(info.PropertyName).IsEqualTo("OrderId");
    await Assert.That(info.PropertyType).IsEqualTo("global::System.Guid");
    await Assert.That(info.IsPropertyValueType).IsTrue();
  }

  [Test]
  public async Task CommandStreamIdInfo_ValueEquality_ComparesFieldsAsync() {
    // Arrange
    var info1 = new CommandStreamIdInfo(
      "global::MyApp.Commands.CreateOrderCommand", "OrderId", "global::System.Guid", true
    );
    var info2 = new CommandStreamIdInfo(
      "global::MyApp.Commands.CreateOrderCommand", "OrderId", "global::System.Guid", true
    );
    var info3 = new CommandStreamIdInfo(
      "global::MyApp.Commands.UpdateOrderCommand", "OrderId", "global::System.Guid", true
    );

    // Assert
    await Assert.That(info1).IsEqualTo(info2);
    await Assert.That(info1).IsNotEqualTo(info3);
    await Assert.That(info1.GetHashCode()).IsEqualTo(info2.GetHashCode());
  }

  [Test]
  public async Task CommandStreamIdInfo_Deconstruction_WorksCorrectlyAsync() {
    // Arrange
    var info = new CommandStreamIdInfo(
      "global::MyApp.Commands.CreateProductCommand",
      "ProductId",
      "global::MyApp.ProductId",
      true
    );

    // Act
    var (commandType, propertyName, propertyType, isPropertyValueType) = info;

    // Assert
    await Assert.That(commandType).IsEqualTo("global::MyApp.Commands.CreateProductCommand");
    await Assert.That(propertyName).IsEqualTo("ProductId");
    await Assert.That(propertyType).IsEqualTo("global::MyApp.ProductId");
    await Assert.That(isPropertyValueType).IsTrue();
  }

}
