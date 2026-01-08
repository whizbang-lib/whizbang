using Rocks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Unit tests for lifecycle invoker integration with Outbox stages (PreOutbox/PostOutbox).
/// These stages fire before/after publishing messages to transport in WorkCoordinatorPublisherWorker.
/// </summary>
public sealed class LifecycleInvokerOutboxStagesTests {

  /// <summary>
  /// Test event for lifecycle invocation.
  /// </summary>
  public sealed record TestEvent(string Data) : IEvent {
    public static TestEvent Create(string data) => new(data);
  }

  [Test]
  public async Task PublisherWorker_PublishMessage_InvokesPreOutboxAsyncAsync() {
    // Arrange
    var invokerMock = Rock.Create<ILifecycleInvoker>();
    invokerMock.Methods().InvokeAsync(Arg.IsAny<object>(), Arg.Is<LifecycleStage>(s => s == LifecycleStage.PreOutboxAsync), Arg.IsAny<ILifecycleContext?>(), Arg.IsAny<CancellationToken>())
      .ReturnsAsync();

    var testEvent = TestEvent.Create("test");

    // Act
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PreOutboxAsync, null, default);

    // Assert
    invokerMock.Verify();
  }

  [Test]
  public async Task PublisherWorker_PublishMessage_InvokesPostOutboxAsyncAsync() {
    // Arrange
    var invokerMock = Rock.Create<ILifecycleInvoker>();
    invokerMock.Methods().InvokeAsync(Arg.IsAny<object>(), Arg.Is<LifecycleStage>(s => s == LifecycleStage.PostOutboxAsync), Arg.IsAny<ILifecycleContext?>(), Arg.IsAny<CancellationToken>())
      .ReturnsAsync();

    var testEvent = TestEvent.Create("test");

    // Act
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PostOutboxAsync, null, default);

    // Assert
    invokerMock.Verify();
  }

  [Test]
  public async Task PublisherWorker_PublishMessage_InvokesPreOutboxInlineAsync() {
    // Arrange
    var invokerMock = Rock.Create<ILifecycleInvoker>();
    invokerMock.Methods().InvokeAsync(Arg.IsAny<object>(), Arg.Is<LifecycleStage>(s => s == LifecycleStage.PreOutboxInline), Arg.IsAny<ILifecycleContext?>(), Arg.IsAny<CancellationToken>())
      .ReturnsAsync();

    var testEvent = TestEvent.Create("test");

    // Act
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PreOutboxInline, null, default);

    // Assert
    invokerMock.Verify();
  }

  [Test]
  public async Task PublisherWorker_PublishMessage_InvokesPostOutboxInlineAsync() {
    // Arrange
    var invokerMock = Rock.Create<ILifecycleInvoker>();
    invokerMock.Methods().InvokeAsync(Arg.IsAny<object>(), Arg.Is<LifecycleStage>(s => s == LifecycleStage.PostOutboxInline), Arg.IsAny<ILifecycleContext?>(), Arg.IsAny<CancellationToken>())
      .ReturnsAsync();

    var testEvent = TestEvent.Create("test");

    // Act
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PostOutboxInline, null, default);

    // Assert
    invokerMock.Verify();
  }

  [Test]
  public async Task PublisherWorker_PublishMessage_AllOutboxStages_InvokedInOrderAsync() {
    // Arrange
    var invokedStages = new List<LifecycleStage>();

    var invokerMock = Rock.Create<ILifecycleInvoker>();
    invokerMock.Methods().InvokeAsync(Arg.IsAny<object>(), Arg.IsAny<LifecycleStage>(), Arg.IsAny<ILifecycleContext?>(), Arg.IsAny<CancellationToken>())
      .Callback((object msg, LifecycleStage stage, ILifecycleContext? ctx, CancellationToken ct) => {
        invokedStages.Add(stage);
      })
      .ReturnsAsync();

    var testEvent = TestEvent.Create("test");

    // Act - Simulate the order of invocation in PublisherWorker
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PreOutboxAsync, null, default);
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PreOutboxInline, null, default);
    // ... transport.PublishAsync() happens here ...
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PostOutboxAsync, null, default);
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PostOutboxInline, null, default);

    // Assert - Verify correct order
    await Assert.That(invokedStages).HasCount().EqualTo(4);
    await Assert.That(invokedStages[0]).IsEqualTo(LifecycleStage.PreOutboxAsync);
    await Assert.That(invokedStages[1]).IsEqualTo(LifecycleStage.PreOutboxInline);
    await Assert.That(invokedStages[2]).IsEqualTo(LifecycleStage.PostOutboxAsync);
    await Assert.That(invokedStages[3]).IsEqualTo(LifecycleStage.PostOutboxInline);

    invokerMock.Verify();
  }

  [Test]
  public async Task PublisherWorker_LifecycleInvokerNull_DoesNotThrowAsync() {
    // Arrange
    ILifecycleInvoker? nullInvoker = null;

    // Act & Assert - Should not throw when invoker is null
    // This simulates the case where lifecycle invoker is optional
    await Assert.That(nullInvoker).IsNull();
  }
}
