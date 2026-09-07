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
      []
    );
    var info2 = new ReceptorInfo(
      "MyApp.Receptors.OrderReceptor",
      "MyApp.Commands.CreateOrder",
      "MyApp.Events.OrderCreated",
      []
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
      []
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
      []
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
      []
    );

    // Assert
    await Assert.That(nonVoidReceptor.IsVoid).IsFalse();
  }

  [Test]
  public async Task ReceptorInfo_Equality_WithDifferentValues_NotEqualAsync() {
    // Arrange - Create instances with different values
    var info1 = new ReceptorInfo("Class1", "Message1", "Response1", []);
    var info2 = new ReceptorInfo("Class2", "Message1", "Response1", []);  // Different ClassName
    var info3 = new ReceptorInfo("Class1", "Message2", "Response1", []);  // Different MessageType
    var info4 = new ReceptorInfo("Class1", "Message1", "Response2", []);  // Different ResponseType
    var info5 = new ReceptorInfo("Class1", "Message1", null, []);         // Different ResponseType (null)

    // Act & Assert - Instances with different values are not equal
    await Assert.That(info1).IsNotEqualTo(info2);
    await Assert.That(info1).IsNotEqualTo(info3);
    await Assert.That(info1).IsNotEqualTo(info4);
    await Assert.That(info1).IsNotEqualTo(info5);
  }

  [Test]
  public async Task ReceptorInfo_GetHashCode_SameForEqualInstancesAsync() {
    // Arrange - Create two equal instances
    var info1 = new ReceptorInfo("MyClass", "MyMessage", "MyResponse", []);
    var info2 = new ReceptorInfo("MyClass", "MyMessage", "MyResponse", []);

    // Act
    var hash1 = info1.GetHashCode();
    var hash2 = info2.GetHashCode();

    // Assert - Hash codes match for equal instances
    await Assert.That(hash1).IsEqualTo(hash2);
  }

  [Test]
  public async Task ReceptorInfo_IsPolymorphicMessageType_DefaultsFalseAsync() {
    // Arrange & Act - Default value should be false
    var info = new ReceptorInfo("MyClass", "MyMessage", "MyResponse", []);

    // Assert
    await Assert.That(info.IsPolymorphicMessageType).IsFalse();
  }

  [Test]
  public async Task ReceptorInfo_IsPolymorphicMessageType_TrueWhenSetAsync() {
    // Arrange & Act
    var info = new ReceptorInfo(
      "MyClass", "MyMessage", "MyResponse", [],
      IsPolymorphicMessageType: true
    );

    // Assert
    await Assert.That(info.IsPolymorphicMessageType).IsTrue();
  }

  [Test]
  public async Task ReceptorInfo_Equality_IncludesIsPolymorphicMessageTypeAsync() {
    // Arrange
    var info1 = new ReceptorInfo("MyClass", "MyMessage", "MyResponse", [], IsPolymorphicMessageType: false);
    var info2 = new ReceptorInfo("MyClass", "MyMessage", "MyResponse", [], IsPolymorphicMessageType: true);

    // Assert - Different IsPolymorphicMessageType means not equal
    await Assert.That(info1).IsNotEqualTo(info2);
  }

  // HasSyncAttributes distinguishes THREE states of the SyncAttributes slot: absent (null),
  // present-but-empty, and populated. The generator's extraction returns null when a receptor
  // carries no [AwaitPerspectiveSync], but an empty array is what a future extraction change would
  // most naturally produce instead — and a regression to a plain null check would then claim the
  // receptor awaits a perspective sync it never declared, making the dispatcher wait on a sync
  // barrier that nothing will ever satisfy.
  [Test]
  public async Task ReceptorInfo_HasSyncAttributes_IsFalseWhenTheSlotIsNullAsync() {
    var info = new ReceptorInfo("MyClass", "MyMessage", null, []);

    await Assert.That(info.SyncAttributes).IsNull()
      .Because("the default for the SyncAttributes slot is null, not an empty array");
    await Assert.That(info.HasSyncAttributes).IsFalse();
  }

  [Test]
  public async Task ReceptorInfo_HasSyncAttributes_IsFalseWhenTheSlotIsAnEmptyArrayAsync() {
    var info = new ReceptorInfo(
      "MyClass", "MyMessage", null, [],
      SyncAttributes: []
    );

    await Assert.That(info.HasSyncAttributes).IsFalse()
      .Because("an empty array declares no sync barriers, so it must read the same as no attribute at all");
  }

  [Test]
  public async Task ReceptorInfo_HasSyncAttributes_IsTrueWhenAtLeastOneAttributeIsPresentAsync() {
    var info = new ReceptorInfo(
      "MyClass", "MyMessage", null, [],
      SyncAttributes: [new SyncAttributeInfo("MyApp.Perspectives.OrderView", null, 5000, 0)]
    );

    await Assert.That(info.HasSyncAttributes).IsTrue();
  }
}
