using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for ILifecycleContext and LifecycleExecutionContext.
/// Tests verify context properties, optional injection, and immutability.
/// </summary>
public class LifecycleContextTests {
  /// <summary>
  /// Tests that LifecycleExecutionContext stores all properties correctly.
  /// </summary>
  [Test]
  public async Task LifecycleExecutionContext_Constructor_StoresAllPropertiesAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    var lastProcessedEventId = Guid.NewGuid();
    var stage = LifecycleStage.PostPerspectiveAsync;

    // Act
    var context = new LifecycleExecutionContext {
      CurrentStage = stage,
      EventId = eventId,
      StreamId = streamId,
      LastProcessedEventId = lastProcessedEventId
    };

    // Assert
    await Assert.That(context.CurrentStage).IsEqualTo(stage);
    await Assert.That(context.EventId).IsEqualTo(eventId);
    await Assert.That(context.StreamId).IsEqualTo(streamId);
    await Assert.That(context.LastProcessedEventId).IsEqualTo(lastProcessedEventId);
  }

  /// <summary>
  /// Tests that LifecycleExecutionContext allows null values for optional properties.
  /// </summary>
  [Test]
  public async Task LifecycleExecutionContext_OptionalProperties_CanBeNullAsync() {
    // Arrange & Act
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.ImmediateAsync,
      EventId = null,
      StreamId = null,
      LastProcessedEventId = null
    };

    // Assert
    await Assert.That(context.EventId).IsNull();
    await Assert.That(context.StreamId).IsNull();
    await Assert.That(context.PerspectiveType).IsNull();
    await Assert.That(context.LastProcessedEventId).IsNull();
  }

  /// <summary>
  /// Tests that LifecycleExecutionContext can be used for perspective processing.
  /// </summary>
  [Test]
  public async Task LifecycleExecutionContext_PerspectiveScenario_HasRequiredPropertiesAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var lastProcessedEventId = Guid.NewGuid();
    var perspectiveType = typeof(TestPerspective);

    // Act
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      StreamId = streamId,
      PerspectiveType = perspectiveType,
      LastProcessedEventId = lastProcessedEventId
    };

    // Assert - Verify perspective-specific properties are set
    await Assert.That(context.StreamId).IsEqualTo(streamId);
    await Assert.That(context.PerspectiveType).IsEqualTo(perspectiveType);
    await Assert.That(context.PerspectiveType?.Name).IsEqualTo("TestPerspective");
    await Assert.That(context.LastProcessedEventId).IsEqualTo(lastProcessedEventId);
    await Assert.That(context.CurrentStage).IsEqualTo(LifecycleStage.PostPerspectiveInline);
  }

  /// <summary>
  /// Tests that LifecycleExecutionContext can be used for immediate dispatch.
  /// </summary>
  [Test]
  public async Task LifecycleExecutionContext_ImmediateScenario_MinimalPropertiesAsync() {
    // Arrange & Act
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.ImmediateAsync
    };

    // Assert - Immediate dispatch doesn't need perspective/stream info
    await Assert.That(context.CurrentStage).IsEqualTo(LifecycleStage.ImmediateAsync);
    await Assert.That(context.StreamId).IsNull();
    await Assert.That(context.PerspectiveType).IsNull();
  }

  /// <summary>
  /// Tests that receptor can optionally inject ILifecycleContext in constructor.
  /// </summary>
  [Test]
  public async Task Receptor_WithLifecycleContext_CanAccessContextPropertiesAsync() {
    // Arrange
    var perspectiveType = typeof(TestPerspective);
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveAsync,
      PerspectiveType = perspectiveType
    };

    var receptor = new TestReceptorWithContext(context);

    // Act
    await receptor.HandleAsync(new TestMessage(), CancellationToken.None);

    // Assert - Receptor accessed context during handling
    await Assert.That(receptor.ReceivedStage).IsEqualTo(LifecycleStage.PostPerspectiveAsync);
    await Assert.That(receptor.ReceivedPerspectiveType).IsEqualTo(perspectiveType);
  }

  // Test receptor that injects ILifecycleContext
  internal sealed class TestReceptorWithContext : IReceptor<TestMessage> {
    private readonly ILifecycleContext? _context;

    public LifecycleStage? ReceivedStage { get; private set; }
    public Type? ReceivedPerspectiveType { get; private set; }

    public TestReceptorWithContext(ILifecycleContext? context = null) {
      _context = context;
    }

    public ValueTask HandleAsync(TestMessage message, CancellationToken cancellationToken = default) {
      if (_context != null) {
        ReceivedStage = _context.CurrentStage;
        ReceivedPerspectiveType = _context.PerspectiveType;
      }
      return ValueTask.CompletedTask;
    }
  }

  internal sealed record TestMessage : IMessage;

  // Test perspective for type testing
  internal sealed class TestPerspective { }
}
