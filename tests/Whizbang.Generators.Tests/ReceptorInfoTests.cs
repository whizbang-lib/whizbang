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

  #region Trace Attribute Tests

  [Test]
  public async Task ReceptorInfo_HasTraceAttribute_DefaultsToFalseAsync() {
    // Arrange & Act - Create receptor without trace attribute
    var info = new ReceptorInfo(
      "MyApp.Receptors.OrderReceptor",
      "MyApp.Commands.CreateOrder",
      "MyApp.Events.OrderCreated",
      Array.Empty<string>()
    );

    // Assert
    await Assert.That(info.HasTraceAttribute).IsFalse();
    await Assert.That(info.TraceVerbosity).IsNull();
  }

  [Test]
  public async Task ReceptorInfo_HasTraceAttribute_SetToTrueAsync() {
    // Arrange & Act - Create receptor with trace attribute
    var info = new ReceptorInfo(
      "MyApp.Receptors.OrderReceptor",
      "MyApp.Commands.CreateOrder",
      "MyApp.Events.OrderCreated",
      Array.Empty<string>(),
      HasTraceAttribute: true,
      TraceVerbosity: 3  // TraceVerbosity.Verbose
    );

    // Assert
    await Assert.That(info.HasTraceAttribute).IsTrue();
    await Assert.That(info.TraceVerbosity).IsEqualTo(3);
  }

  [Test]
  public async Task ReceptorInfo_TraceVerbosity_SupportsAllLevelsAsync() {
    // TraceVerbosity enum values: 0=Off, 1=Minimal, 2=Normal, 3=Verbose, 4=Debug

    // Arrange & Act - Test all verbosity levels
    var infoOff = new ReceptorInfo("C", "M", "R", Array.Empty<string>(), HasTraceAttribute: true, TraceVerbosity: 0);
    var infoMinimal = new ReceptorInfo("C", "M", "R", Array.Empty<string>(), HasTraceAttribute: true, TraceVerbosity: 1);
    var infoNormal = new ReceptorInfo("C", "M", "R", Array.Empty<string>(), HasTraceAttribute: true, TraceVerbosity: 2);
    var infoVerbose = new ReceptorInfo("C", "M", "R", Array.Empty<string>(), HasTraceAttribute: true, TraceVerbosity: 3);
    var infoDebug = new ReceptorInfo("C", "M", "R", Array.Empty<string>(), HasTraceAttribute: true, TraceVerbosity: 4);

    // Assert
    await Assert.That(infoOff.TraceVerbosity).IsEqualTo(0);
    await Assert.That(infoMinimal.TraceVerbosity).IsEqualTo(1);
    await Assert.That(infoNormal.TraceVerbosity).IsEqualTo(2);
    await Assert.That(infoVerbose.TraceVerbosity).IsEqualTo(3);
    await Assert.That(infoDebug.TraceVerbosity).IsEqualTo(4);
  }

  [Test]
  public async Task ReceptorInfo_ValueEquality_IncludesTraceAttributeAsync() {
    // Arrange - Create instances that differ only in trace attribute
    var info1 = new ReceptorInfo("C", "M", "R", Array.Empty<string>(), HasTraceAttribute: false);
    var info2 = new ReceptorInfo("C", "M", "R", Array.Empty<string>(), HasTraceAttribute: true, TraceVerbosity: 3);

    // Assert - Different trace attributes means not equal
    await Assert.That(info1).IsNotEqualTo(info2);
  }

  [Test]
  public async Task ReceptorInfo_ValueEquality_IncludesTraceVerbosityAsync() {
    // Arrange - Create instances that differ only in trace verbosity
    var info1 = new ReceptorInfo("C", "M", "R", Array.Empty<string>(), HasTraceAttribute: true, TraceVerbosity: 3);
    var info2 = new ReceptorInfo("C", "M", "R", Array.Empty<string>(), HasTraceAttribute: true, TraceVerbosity: 4);

    // Assert - Different verbosity means not equal (important for caching)
    await Assert.That(info1).IsNotEqualTo(info2);
  }

  #endregion

  #region Metric Attribute Tests

  [Test]
  public async Task ReceptorInfo_HasMetricAttribute_DefaultsToFalseAsync() {
    // Arrange & Act - Create receptor without metric attribute
    var info = new ReceptorInfo(
      "MyApp.Receptors.OrderReceptor",
      "MyApp.Commands.CreateOrder",
      "MyApp.Events.OrderCreated",
      Array.Empty<string>()
    );

    // Assert
    await Assert.That(info.HasMetricAttribute).IsFalse();
  }

  [Test]
  public async Task ReceptorInfo_HasMetricAttribute_SetToTrueAsync() {
    // Arrange & Act - Create receptor with metric attribute
    var info = new ReceptorInfo(
      "MyApp.Receptors.OrderReceptor",
      "MyApp.Commands.CreateOrder",
      "MyApp.Events.OrderCreated",
      Array.Empty<string>(),
      HasMetricAttribute: true
    );

    // Assert
    await Assert.That(info.HasMetricAttribute).IsTrue();
  }

  [Test]
  public async Task ReceptorInfo_ValueEquality_IncludesMetricAttributeAsync() {
    // Arrange - Create instances that differ only in metric attribute
    var info1 = new ReceptorInfo("C", "M", "R", Array.Empty<string>(), HasMetricAttribute: false);
    var info2 = new ReceptorInfo("C", "M", "R", Array.Empty<string>(), HasMetricAttribute: true);

    // Assert - Different metric attributes means not equal (important for caching)
    await Assert.That(info1).IsNotEqualTo(info2);
  }

  [Test]
  public async Task ReceptorInfo_BothTraceAndMetric_CanBeSetAsync() {
    // Arrange & Act - Create receptor with both trace and metric attributes
    var info = new ReceptorInfo(
      "MyApp.Receptors.PaymentReceptor",
      "MyApp.Commands.ProcessPayment",
      "MyApp.Events.PaymentProcessed",
      Array.Empty<string>(),
      HasTraceAttribute: true,
      TraceVerbosity: 4,  // Debug
      HasMetricAttribute: true
    );

    // Assert - Both can be set simultaneously
    await Assert.That(info.HasTraceAttribute).IsTrue();
    await Assert.That(info.TraceVerbosity).IsEqualTo(4);
    await Assert.That(info.HasMetricAttribute).IsTrue();
  }

  #endregion
}
