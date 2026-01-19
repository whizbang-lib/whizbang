namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for AggregateIdInfo - ensures value equality for incremental generator caching.
/// </summary>
public class AggregateIdInfoTests {

  [Test]
  public async Task AggregateIdInfo_ValueEquality_ComparesFieldsAsync() {
    // Arrange - Create two instances with same values
    var info1 = new AggregateIdInfo(
      "global::MyApp.Commands.CreateOrder",
      "OrderId",
      false,
      false,
      false
    );
    var info2 = new AggregateIdInfo(
      "global::MyApp.Commands.CreateOrder",
      "OrderId",
      false,
      false,
      false
    );

    // Act & Assert - Records use value equality
    await Assert.That(info1).IsEqualTo(info2);
    await Assert.That(info1.GetHashCode()).IsEqualTo(info2.GetHashCode());
  }

  [Test]
  public async Task AggregateIdInfo_Constructor_SetsPropertiesAsync() {
    // Arrange & Act
    var info = new AggregateIdInfo(
      "global::MyApp.Commands.UpdateProduct",
      "ProductId",
      true,  // IsNullable
      false, // HasMultipleAttributes
      false  // HasInvalidType
    );

    // Assert
    await Assert.That(info.MessageType).IsEqualTo("global::MyApp.Commands.UpdateProduct");
    await Assert.That(info.PropertyName).IsEqualTo("ProductId");
    await Assert.That(info.IsNullable).IsTrue();
    await Assert.That(info.HasMultipleAttributes).IsFalse();
    await Assert.That(info.HasInvalidType).IsFalse();
  }

  [Test]
  public async Task AggregateIdInfo_ErrorFlags_TrackValidationStatesAsync() {
    // Arrange & Act - Create info with error flags set
    var infoWithMultiple = new AggregateIdInfo(
      "global::MyApp.Commands.CreateOrder",
      "OrderId",
      false,
      HasMultipleAttributes: true,
      HasInvalidType: false
    );

    var infoWithInvalidType = new AggregateIdInfo(
      "global::MyApp.Commands.CreateOrder",
      "OrderId",
      false,
      HasMultipleAttributes: false,
      HasInvalidType: true
    );

    var infoWithBothErrors = new AggregateIdInfo(
      "global::MyApp.Commands.CreateOrder",
      "OrderId",
      false,
      HasMultipleAttributes: true,
      HasInvalidType: true
    );

    // Assert - Error flags are tracked correctly
    await Assert.That(infoWithMultiple.HasMultipleAttributes).IsTrue();
    await Assert.That(infoWithMultiple.HasInvalidType).IsFalse();

    await Assert.That(infoWithInvalidType.HasMultipleAttributes).IsFalse();
    await Assert.That(infoWithInvalidType.HasInvalidType).IsTrue();

    await Assert.That(infoWithBothErrors.HasMultipleAttributes).IsTrue();
    await Assert.That(infoWithBothErrors.HasInvalidType).IsTrue();
  }
}
