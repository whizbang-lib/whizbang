using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Generators.Tests.Models;

/// <summary>
/// Tests for the PerspectiveInfo value record.
/// Ensures proper value equality semantics and immutability.
/// </summary>
public class PerspectiveInfoTests {

  [Test]
  public async Task PerspectiveInfo_WithSameValues_AreEqualAsync() {
    // Arrange
    var info1 = new PerspectiveInfo(
      HandlerType: "global::MyApp.ProductPerspective",
      EventType: "global::MyApp.Events.ProductCreated",
      StateType: "global::MyApp.ProductDto",
      TableName: "product_dtos",
      StreamKeyType: "global::MyApp.ProductId"
    );

    var info2 = new PerspectiveInfo(
      HandlerType: "global::MyApp.ProductPerspective",
      EventType: "global::MyApp.Events.ProductCreated",
      StateType: "global::MyApp.ProductDto",
      TableName: "product_dtos",
      StreamKeyType: "global::MyApp.ProductId"
    );

    // Act & Assert - Value equality should work
    await Assert.That(info1).IsEqualTo(info2);
    await Assert.That(info1.GetHashCode()).IsEqualTo(info2.GetHashCode());
  }

  [Test]
  public async Task PerspectiveInfo_WithDifferentHandlerType_AreNotEqualAsync() {
    // Arrange
    var info1 = new PerspectiveInfo(
      HandlerType: "global::MyApp.ProductPerspective",
      EventType: "global::MyApp.Events.ProductCreated",
      StateType: "global::MyApp.ProductDto",
      TableName: "product_dtos"
    );

    var info2 = new PerspectiveInfo(
      HandlerType: "global::MyApp.OrderPerspective",
      EventType: "global::MyApp.Events.ProductCreated",
      StateType: "global::MyApp.ProductDto",
      TableName: "product_dtos"
    );

    // Act & Assert
    await Assert.That(info1).IsNotEqualTo(info2);
  }

  [Test]
  public async Task PerspectiveInfo_WithNullableHandlerType_WorksCorrectlyAsync() {
    // Arrange - HandlerType nullable when discovered from DbSet only
    var info = new PerspectiveInfo(
      HandlerType: null,
      EventType: "global::MyApp.Events.ProductCreated",
      StateType: "global::MyApp.ProductDto",
      TableName: "product_dtos"
    );

    // Act & Assert
    await Assert.That(info.HandlerType).IsNull();
    await Assert.That(info.StateType).IsEqualTo("global::MyApp.ProductDto");
  }

  [Test]
  public async Task PerspectiveInfo_WithNullableEventType_WorksCorrectlyAsync() {
    // Arrange - EventType nullable when discovered from DbSet only
    var info = new PerspectiveInfo(
      HandlerType: "global::MyApp.ProductPerspective",
      EventType: null,
      StateType: "global::MyApp.ProductDto",
      TableName: "product_dtos"
    );

    // Act & Assert
    await Assert.That(info.EventType).IsNull();
    await Assert.That(info.StateType).IsEqualTo("global::MyApp.ProductDto");
  }

  [Test]
  public async Task PerspectiveInfo_WithNullableStreamKeyType_WorksCorrectlyAsync() {
    // Arrange - StreamKeyType nullable for non-aggregate perspectives
    var info = new PerspectiveInfo(
      HandlerType: "global::MyApp.ProductPerspective",
      EventType: "global::MyApp.Events.ProductCreated",
      StateType: "global::MyApp.ProductDto",
      TableName: "product_dtos",
      StreamKeyType: null
    );

    // Act & Assert
    await Assert.That(info.StreamKeyType).IsNull();
  }

  [Test]
  public async Task PerspectiveInfo_DefaultStreamKeyType_IsNullAsync() {
    // Arrange - StreamKeyType defaults to null when not provided
    var info = new PerspectiveInfo(
      HandlerType: "global::MyApp.ProductPerspective",
      EventType: "global::MyApp.Events.ProductCreated",
      StateType: "global::MyApp.ProductDto",
      TableName: "product_dtos"
    );

    // Act & Assert
    await Assert.That(info.StreamKeyType).IsNull();
  }

  [Test]
  public async Task PerspectiveInfo_Properties_AreAccessibleAsync() {
    // Arrange
    var info = new PerspectiveInfo(
      HandlerType: "global::MyApp.ProductPerspective",
      EventType: "global::MyApp.Events.ProductCreated",
      StateType: "global::MyApp.ProductDto",
      TableName: "product_dtos",
      StreamKeyType: "global::MyApp.ProductId"
    );

    // Act & Assert
    await Assert.That(info.HandlerType).IsEqualTo("global::MyApp.ProductPerspective");
    await Assert.That(info.EventType).IsEqualTo("global::MyApp.Events.ProductCreated");
    await Assert.That(info.StateType).IsEqualTo("global::MyApp.ProductDto");
    await Assert.That(info.TableName).IsEqualTo("product_dtos");
    await Assert.That(info.StreamKeyType).IsEqualTo("global::MyApp.ProductId");
  }

  [Test]
  public async Task PerspectiveInfo_WithDifferentTableName_AreNotEqualAsync() {
    // Arrange
    var info1 = new PerspectiveInfo(
      HandlerType: "global::MyApp.ProductPerspective",
      EventType: "global::MyApp.Events.ProductCreated",
      StateType: "global::MyApp.ProductDto",
      TableName: "product_dtos"
    );

    var info2 = new PerspectiveInfo(
      HandlerType: "global::MyApp.ProductPerspective",
      EventType: "global::MyApp.Events.ProductCreated",
      StateType: "global::MyApp.ProductDto",
      TableName: "Products"
    );

    // Act & Assert
    await Assert.That(info1).IsNotEqualTo(info2);
  }

  [Test]
  public async Task PerspectiveInfo_ValueSemantics_CriticalForIncrementalGeneratorCachingAsync() {
    // Arrange - Verify that value equality works correctly for caching
    var info1 = new PerspectiveInfo(
      HandlerType: "global::MyApp.ProductPerspective",
      EventType: "global::MyApp.Events.ProductCreated",
      StateType: "global::MyApp.ProductDto",
      TableName: "product_dtos"
    );

    var info2 = new PerspectiveInfo(
      HandlerType: "global::MyApp.ProductPerspective",
      EventType: "global::MyApp.Events.ProductCreated",
      StateType: "global::MyApp.ProductDto",
      TableName: "product_dtos"
    );

    // Act & Assert - Critical for incremental generator performance
    // If these aren't equal, generator will regenerate unnecessarily
    await Assert.That(info1).IsEqualTo(info2);
    await Assert.That(info1.GetHashCode()).IsEqualTo(info2.GetHashCode());
  }
}
