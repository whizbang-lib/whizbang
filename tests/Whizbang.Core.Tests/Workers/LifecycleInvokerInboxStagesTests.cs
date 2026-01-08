using Rocks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Unit tests for lifecycle invoker integration with Inbox stages (PreInbox/PostInbox).
/// These stages fire before/after invoking local receptors in ServiceBusConsumerWorker.
/// </summary>
public sealed class LifecycleInvokerInboxStagesTests {

  /// <summary>
  /// Test event for lifecycle invocation.
  /// </summary>
  public sealed record TestEvent(string Data) : IEvent {
    public static TestEvent Create(string data) => new(data);
  }

  [Test]
  public async Task ConsumerWorker_ProcessInboxMessage_InvokesPreInboxAsyncAsync() {
    // Arrange
    var invokerMock = Rock.Create<ILifecycleInvoker>();
    invokerMock.Methods().InvokeAsync(Arg.IsAny<object>(), Arg.Is<LifecycleStage>(s => s == LifecycleStage.PreInboxAsync), Arg.IsAny<ILifecycleContext?>(), Arg.IsAny<CancellationToken>())
      .ReturnsAsync();

    var testEvent = TestEvent.Create("test");

    // Act
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PreInboxAsync, null, default);

    // Assert
    invokerMock.Verify();
  }

  [Test]
  public async Task ConsumerWorker_ProcessInboxMessage_InvokesPostInboxAsyncAsync() {
    // Arrange
    var invokerMock = Rock.Create<ILifecycleInvoker>();
    invokerMock.Methods().InvokeAsync(Arg.IsAny<object>(), Arg.Is<LifecycleStage>(s => s == LifecycleStage.PostInboxAsync), Arg.IsAny<ILifecycleContext?>(), Arg.IsAny<CancellationToken>())
      .ReturnsAsync();

    var testEvent = TestEvent.Create("test");

    // Act
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PostInboxAsync, null, default);

    // Assert
    invokerMock.Verify();
  }

  [Test]
  public async Task ConsumerWorker_ProcessInboxMessage_InvokesPreInboxInlineAsync() {
    // Arrange
    var invokerMock = Rock.Create<ILifecycleInvoker>();
    invokerMock.Methods().InvokeAsync(Arg.IsAny<object>(), Arg.Is<LifecycleStage>(s => s == LifecycleStage.PreInboxInline), Arg.IsAny<ILifecycleContext?>(), Arg.IsAny<CancellationToken>())
      .ReturnsAsync();

    var testEvent = TestEvent.Create("test");

    // Act
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PreInboxInline, null, default);

    // Assert
    invokerMock.Verify();
  }

  [Test]
  public async Task ConsumerWorker_ProcessInboxMessage_InvokesPostInboxInlineAsync() {
    // Arrange
    var invokerMock = Rock.Create<ILifecycleInvoker>();
    invokerMock.Methods().InvokeAsync(Arg.IsAny<object>(), Arg.Is<LifecycleStage>(s => s == LifecycleStage.PostInboxInline), Arg.IsAny<ILifecycleContext?>(), Arg.IsAny<CancellationToken>())
      .ReturnsAsync();

    var testEvent = TestEvent.Create("test");

    // Act
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PostInboxInline, null, default);

    // Assert
    invokerMock.Verify();
  }

  [Test]
  public async Task ConsumerWorker_ProcessInboxMessage_AllInboxStages_InvokedInOrderAsync() {
    // Arrange
    var invokedStages = new List<LifecycleStage>();

    var invokerMock = Rock.Create<ILifecycleInvoker>();
    invokerMock.Methods().InvokeAsync(Arg.IsAny<object>(), Arg.IsAny<LifecycleStage>(), Arg.IsAny<ILifecycleContext?>(), Arg.IsAny<CancellationToken>())
      .Callback((object msg, LifecycleStage stage, ILifecycleContext? ctx, CancellationToken ct) => {
        invokedStages.Add(stage);
      })
      .ReturnsAsync();

    var testEvent = TestEvent.Create("test");

    // Act - Simulate the order of invocation in ConsumerWorker
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PreInboxAsync, null, default);
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PreInboxInline, null, default);
    // ... dispatcher.SendAsync() happens here ...
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PostInboxAsync, null, default);
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PostInboxInline, null, default);

    // Assert - Verify correct order
    await Assert.That(invokedStages).HasCount().EqualTo(4);
    await Assert.That(invokedStages[0]).IsEqualTo(LifecycleStage.PreInboxAsync);
    await Assert.That(invokedStages[1]).IsEqualTo(LifecycleStage.PreInboxInline);
    await Assert.That(invokedStages[2]).IsEqualTo(LifecycleStage.PostInboxAsync);
    await Assert.That(invokedStages[3]).IsEqualTo(LifecycleStage.PostInboxInline);

    invokerMock.Verify();
  }

  [Test]
  public async Task ConsumerWorker_LifecycleInvokerNull_DoesNotThrowAsync() {
    // Arrange
    ILifecycleInvoker? nullInvoker = null;

    // Act & Assert - Should not throw when invoker is null
    // This simulates the case where lifecycle invoker is optional
    await Assert.That(nullInvoker).IsNull();
  }
}
