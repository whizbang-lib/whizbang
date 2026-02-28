namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for ReceptorInfo - ensures value equality for incremental generator caching.
/// ReceptorInfo is a sealed record used to cache discovered receptor information during source generation.
/// Value equality is critical for incremental generator performance.
/// </summary>
public class ReceptorInfoTests {

  [Test]
  public async Task ReceptorInfo_ValueEquality_ComparesFieldsAsync() {
    // Arrange - Create two instances with same values
    var info1 = new ReceptorInfo(
      "MyApp.Receptors.OrderReceptor",
      "MyApp.Commands.CreateOrder",
      "MyApp.Events.OrderCreated",
      Array.Empty<string>()
    );
    var info2 = new ReceptorInfo(
      "MyApp.Receptors.OrderReceptor",
      "MyApp.Commands.CreateOrder",
      "MyApp.Events.OrderCreated",
      Array.Empty<string>()
    );

    // Act & Assert - Records use value equality
    await Assert.That(info1).IsEqualTo(info2);
  }

  [Test]
  public async Task ReceptorInfo_Constructor_SetsPropertiesAsync() {
    // Arrange & Act
    var info = new ReceptorInfo(
      "MyApp.Receptors.ProductReceptor",
      "MyApp.Commands.UpdateProduct",
      "MyApp.Events.ProductUpdated",
      Array.Empty<string>()
    );

    // Assert - Verify all properties are set correctly
    await Assert.That(info.ClassName).IsEqualTo("MyApp.Receptors.ProductReceptor");
    await Assert.That(info.MessageType).IsEqualTo("MyApp.Commands.UpdateProduct");
    await Assert.That(info.ResponseType).IsEqualTo("MyApp.Events.ProductUpdated");
  }

  [Test]
  public async Task ReceptorInfo_IsVoid_ReturnsTrueWhenResponseTypeIsNullAsync() {
    // Arrange & Act - Void receptor (IReceptor<TMessage>)
    var voidReceptor = new ReceptorInfo(
      "MyApp.Receptors.NotificationReceptor",
      "MyApp.Commands.SendEmail",
      null,  // No response type
      Array.Empty<string>()
    );

    // Assert
    await Assert.That(voidReceptor.IsVoid).IsTrue();
  }

  [Test]
  public async Task ReceptorInfo_IsVoid_ReturnsFalseWhenResponseTypeIsNotNullAsync() {
    // Arrange & Act - Non-void receptor (IReceptor<TMessage, TResponse>)
    var nonVoidReceptor = new ReceptorInfo(
      "MyApp.Receptors.OrderReceptor",
      "MyApp.Commands.CreateOrder",
      "MyApp.Events.OrderCreated",
      Array.Empty<string>()
    );

    // Assert
    await Assert.That(nonVoidReceptor.IsVoid).IsFalse();
  }

  [Test]
  public async Task ReceptorInfo_Equality_WithDifferentValues_NotEqualAsync() {
    // Arrange - Create instances with different values
    var info1 = new ReceptorInfo("Class1", "Message1", "Response1", Array.Empty<string>());
    var info2 = new ReceptorInfo("Class2", "Message1", "Response1", Array.Empty<string>());  // Different ClassName
    var info3 = new ReceptorInfo("Class1", "Message2", "Response1", Array.Empty<string>());  // Different MessageType
    var info4 = new ReceptorInfo("Class1", "Message1", "Response2", Array.Empty<string>());  // Different ResponseType
    var info5 = new ReceptorInfo("Class1", "Message1", null, Array.Empty<string>());         // Different ResponseType (null)

    // Act & Assert - Instances with different values are not equal
    await Assert.That(info1).IsNotEqualTo(info2);
    await Assert.That(info1).IsNotEqualTo(info3);
    await Assert.That(info1).IsNotEqualTo(info4);
    await Assert.That(info1).IsNotEqualTo(info5);
  }

  [Test]
  public async Task ReceptorInfo_GetHashCode_SameForEqualInstancesAsync() {
    // Arrange - Create two equal instances
    var info1 = new ReceptorInfo("MyClass", "MyMessage", "MyResponse", Array.Empty<string>());
    var info2 = new ReceptorInfo("MyClass", "MyMessage", "MyResponse", Array.Empty<string>());

    // Act
    var hash1 = info1.GetHashCode();
    var hash2 = info2.GetHashCode();

    // Assert - Hash codes match for equal instances
    await Assert.That(hash1).IsEqualTo(hash2);
  }
}
