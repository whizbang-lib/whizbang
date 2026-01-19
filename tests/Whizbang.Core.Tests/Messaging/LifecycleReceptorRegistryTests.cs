using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for ILifecycleReceptorRegistry and DefaultLifecycleReceptorRegistry.
/// Tests verify registration, retrieval, unregistration, and concurrent access.
/// </summary>
public class LifecycleReceptorRegistryTests {
  /// <summary>
  /// Tests that registry can register a receptor for a specific message type and lifecycle stage.
  /// </summary>
  [Test]
  public async Task Registry_Register_CanRetrieveReceptorAsync() {
    // Arrange
    var registry = new DefaultLifecycleReceptorRegistry();
    var receptor = new TestReceptor();

    // Act
    registry.Register<TestMessage>(receptor, LifecycleStage.PostPerspectiveAsync);
    var receptors = registry.GetReceptors(typeof(TestMessage), LifecycleStage.PostPerspectiveAsync);

    // Assert
    await Assert.That(receptors.Count).IsEqualTo(1);
    await Assert.That(receptors[0]).IsEqualTo(receptor);
  }

  /// <summary>
  /// Tests that registry returns empty list when no receptors registered.
  /// </summary>
  [Test]
  public async Task Registry_GetReceptors_NoRegistrations_ReturnsEmptyListAsync() {
    // Arrange
    var registry = new DefaultLifecycleReceptorRegistry();

    // Act
    var receptors = registry.GetReceptors(typeof(TestMessage), LifecycleStage.PostPerspectiveAsync);

    // Assert
    await Assert.That(receptors.Count).IsEqualTo(0);
  }

  /// <summary>
  /// Tests that registry can register multiple receptors for same message type and stage.
  /// </summary>
  [Test]
  public async Task Registry_Register_MultipleReceptors_AllRetrievedAsync() {
    // Arrange
    var registry = new DefaultLifecycleReceptorRegistry();
    var receptor1 = new TestReceptor();
    var receptor2 = new TestReceptor();

    // Act
    registry.Register<TestMessage>(receptor1, LifecycleStage.PostPerspectiveAsync);
    registry.Register<TestMessage>(receptor2, LifecycleStage.PostPerspectiveAsync);
    var receptors = registry.GetReceptors(typeof(TestMessage), LifecycleStage.PostPerspectiveAsync);

    // Assert
    await Assert.That(receptors.Count).IsEqualTo(2);
    await Assert.That(receptors).Contains(receptor1);
    await Assert.That(receptors).Contains(receptor2);
  }

  /// <summary>
  /// Tests that registry can unregister a specific receptor.
  /// </summary>
  [Test]
  public async Task Registry_Unregister_RemovesReceptorAsync() {
    // Arrange
    var registry = new DefaultLifecycleReceptorRegistry();
    var receptor = new TestReceptor();
    registry.Register<TestMessage>(receptor, LifecycleStage.PostPerspectiveAsync);

    // Act
    var removed = registry.Unregister<TestMessage>(receptor, LifecycleStage.PostPerspectiveAsync);
    var receptors = registry.GetReceptors(typeof(TestMessage), LifecycleStage.PostPerspectiveAsync);

    // Assert
    await Assert.That(removed).IsTrue();
    await Assert.That(receptors.Count).IsEqualTo(0);
  }

  /// <summary>
  /// Tests that unregistering non-existent receptor returns false.
  /// </summary>
  [Test]
  public async Task Registry_Unregister_NonExistent_ReturnsFalseAsync() {
    // Arrange
    var registry = new DefaultLifecycleReceptorRegistry();
    var receptor = new TestReceptor();

    // Act
    var removed = registry.Unregister<TestMessage>(receptor, LifecycleStage.PostPerspectiveAsync);

    // Assert
    await Assert.That(removed).IsFalse();
  }

  /// <summary>
  /// Tests that registry isolates receptors by lifecycle stage.
  /// </summary>
  [Test]
  public async Task Registry_Register_DifferentStages_IsolatedAsync() {
    // Arrange
    var registry = new DefaultLifecycleReceptorRegistry();
    var receptor1 = new TestReceptor();
    var receptor2 = new TestReceptor();

    // Act
    registry.Register<TestMessage>(receptor1, LifecycleStage.PostPerspectiveAsync);
    registry.Register<TestMessage>(receptor2, LifecycleStage.PostPerspectiveInline);

    var asyncReceptors = registry.GetReceptors(typeof(TestMessage), LifecycleStage.PostPerspectiveAsync);
    var inlineReceptors = registry.GetReceptors(typeof(TestMessage), LifecycleStage.PostPerspectiveInline);

    // Assert
    await Assert.That(asyncReceptors.Count).IsEqualTo(1);
    await Assert.That(asyncReceptors[0]).IsEqualTo(receptor1);
    await Assert.That(inlineReceptors.Count).IsEqualTo(1);
    await Assert.That(inlineReceptors[0]).IsEqualTo(receptor2);
  }

  /// <summary>
  /// Tests that registry handles concurrent registration safely.
  /// </summary>
  [Test]
  public async Task Registry_ConcurrentRegistration_ThreadSafeAsync() {
    // Arrange
    var registry = new DefaultLifecycleReceptorRegistry();
    var receptors = Enumerable.Range(0, 100).Select(_ => new TestReceptor()).ToList();

    // Act - Register 100 receptors concurrently
    var tasks = receptors.Select(r =>
      Task.Run(() => registry.Register<TestMessage>(r, LifecycleStage.PostPerspectiveAsync))
    ).ToArray();

    await Task.WhenAll(tasks);

    var retrievedReceptors = registry.GetReceptors(typeof(TestMessage), LifecycleStage.PostPerspectiveAsync);

    // Assert
    await Assert.That(retrievedReceptors.Count).IsEqualTo(100);
  }

  /// <summary>
  /// Tests that registry handles concurrent unregistration safely.
  /// </summary>
  [Test]
  public async Task Registry_ConcurrentUnregistration_ThreadSafeAsync() {
    // Arrange
    var registry = new DefaultLifecycleReceptorRegistry();
    var receptors = Enumerable.Range(0, 100).Select(_ => new TestReceptor()).ToList();

    // Register all receptors first
    foreach (var receptor in receptors) {
      registry.Register<TestMessage>(receptor, LifecycleStage.PostPerspectiveAsync);
    }

    // Act - Unregister all receptors concurrently
    var tasks = receptors.Select(r =>
      Task.Run(() => registry.Unregister<TestMessage>(r, LifecycleStage.PostPerspectiveAsync))
    ).ToArray();

    await Task.WhenAll(tasks);

    var remainingReceptors = registry.GetReceptors(typeof(TestMessage), LifecycleStage.PostPerspectiveAsync);

    // Assert
    await Assert.That(remainingReceptors.Count).IsEqualTo(0);
  }

  /// <summary>
  /// Tests that registry isolates receptors by message type.
  /// </summary>
  [Test]
  public async Task Registry_Register_DifferentMessageTypes_IsolatedAsync() {
    // Arrange
    var registry = new DefaultLifecycleReceptorRegistry();
    var receptor1 = new TestReceptor();
    var receptor2 = new AnotherTestReceptor();

    // Act
    registry.Register<TestMessage>(receptor1, LifecycleStage.PostPerspectiveAsync);
    registry.Register<AnotherTestMessage>(receptor2, LifecycleStage.PostPerspectiveAsync);

    var testMessageReceptors = registry.GetReceptors(typeof(TestMessage), LifecycleStage.PostPerspectiveAsync);
    var anotherTestMessageReceptors = registry.GetReceptors(typeof(AnotherTestMessage), LifecycleStage.PostPerspectiveAsync);

    // Assert
    await Assert.That(testMessageReceptors.Count).IsEqualTo(1);
    await Assert.That(testMessageReceptors[0]).IsEqualTo(receptor1);
    await Assert.That(anotherTestMessageReceptors.Count).IsEqualTo(1);
    await Assert.That(anotherTestMessageReceptors[0]).IsEqualTo(receptor2);
  }

  // Test receptors
  internal sealed class TestReceptor : IReceptor<TestMessage> {
    public ValueTask HandleAsync(TestMessage message, CancellationToken cancellationToken = default) {
      return ValueTask.CompletedTask;
    }
  }

  internal sealed class AnotherTestReceptor : IReceptor<AnotherTestMessage> {
    public ValueTask HandleAsync(AnotherTestMessage message, CancellationToken cancellationToken = default) {
      return ValueTask.CompletedTask;
    }
  }

  internal sealed record TestMessage : IMessage;
  internal sealed record AnotherTestMessage : IMessage;
}
