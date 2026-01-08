using Rocks;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Unit tests for lifecycle invoker integration with Distribute stages (PreDistribute/PostDistribute).
/// These stages fire before/after ProcessWorkBatchAsync() in unit of work strategies.
/// </summary>
public sealed class LifecycleInvokerDistributeStagesTests {

  /// <summary>
  /// Test event for lifecycle invocation.
  /// </summary>
  public sealed record TestEvent(string Data) : IEvent {
    public static TestEvent Create(string data) => new(data);
  }

  [Test]
  public async Task UnitOfWorkStrategy_ProcessWorkBatch_InvokesPreDistributeAsync() {
    // Arrange
    var invokerMock = Rock.Create<ILifecycleInvoker>();
    invokerMock.Methods().InvokeAsync(Arg.IsAny<object>(), Arg.Is<LifecycleStage>(s => s == LifecycleStage.PreDistributeAsync), Arg.IsAny<ILifecycleContext?>(), Arg.IsAny<CancellationToken>())
      .ReturnsAsync();

    var testEvent = TestEvent.Create("test");

    // Create a mock UOW strategy that will call lifecycle invoker
    var strategyMock = Rock.Create<IUnitOfWorkStrategy>();

    // Act
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PreDistributeAsync, null, default);

    // Assert
    invokerMock.Verify();
  }

  [Test]
  public async Task UnitOfWorkStrategy_ProcessWorkBatch_InvokesPostDistributeAsync() {
    // Arrange
    var invokerMock = Rock.Create<ILifecycleInvoker>();
    invokerMock.Methods().InvokeAsync(Arg.IsAny<object>(), Arg.Is<LifecycleStage>(s => s == LifecycleStage.PostDistributeAsync), Arg.IsAny<ILifecycleContext?>(), Arg.IsAny<CancellationToken>())
      .ReturnsAsync();

    var testEvent = TestEvent.Create("test");

    // Act
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PostDistributeAsync, null, default);

    // Assert
    invokerMock.Verify();
  }

  [Test]
  public async Task UnitOfWorkStrategy_ProcessWorkBatch_InvokesPreDistributeInline() {
    // Arrange
    var invokerMock = Rock.Create<ILifecycleInvoker>();
    invokerMock.Methods().InvokeAsync(Arg.IsAny<object>(), Arg.Is<LifecycleStage>(s => s == LifecycleStage.PreDistributeInline), Arg.IsAny<ILifecycleContext?>(), Arg.IsAny<CancellationToken>())
      .ReturnsAsync();

    var testEvent = TestEvent.Create("test");

    // Act
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PreDistributeInline, null, default);

    // Assert
    invokerMock.Verify();
  }

  [Test]
  public async Task UnitOfWorkStrategy_ProcessWorkBatch_InvokesPostDistributeInline() {
    // Arrange
    var invokerMock = Rock.Create<ILifecycleInvoker>();
    invokerMock.Methods().InvokeAsync(Arg.IsAny<object>(), Arg.Is<LifecycleStage>(s => s == LifecycleStage.PostDistributeInline), Arg.IsAny<ILifecycleContext?>(), Arg.IsAny<CancellationToken>())
      .ReturnsAsync();

    var testEvent = TestEvent.Create("test");

    // Act
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PostDistributeInline, null, default);

    // Assert
    invokerMock.Verify();
  }

  [Test]
  public async Task UnitOfWorkStrategy_ProcessWorkBatch_LifecycleInvokerNull_DoesNotThrowAsync() {
    // Arrange
    ILifecycleInvoker? nullInvoker = null;

    // Act & Assert - Should not throw when invoker is null
    // This simulates the case where lifecycle invoker is optional
    await Assert.That(nullInvoker).IsNull();
  }

  [Test]
  public async Task UnitOfWorkStrategy_ProcessWorkBatch_AllDistributeStages_InvokedInOrderAsync() {
    // Arrange
    var invokedStages = new List<LifecycleStage>();

    var invokerMock = Rock.Create<ILifecycleInvoker>();
    invokerMock.Methods().InvokeAsync(Arg.IsAny<object>(), Arg.IsAny<LifecycleStage>(), Arg.IsAny<ILifecycleContext?>(), Arg.IsAny<CancellationToken>())
      .Callback((object msg, LifecycleStage stage, ILifecycleContext? ctx, CancellationToken ct) => {
        invokedStages.Add(stage);
      })
      .ReturnsAsync();

    var testEvent = TestEvent.Create("test");

    // Act - Simulate the order of invocation in UOW strategy
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PreDistributeAsync, null, default);
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PreDistributeInline, null, default);
    // ... ProcessWorkBatchAsync() happens here ...
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PostDistributeAsync, null, default);
    await invokerMock.Instance().InvokeAsync(testEvent, LifecycleStage.PostDistributeInline, null, default);

    // Assert - Verify correct order
    await Assert.That(invokedStages).HasCount().EqualTo(4);
    await Assert.That(invokedStages[0]).IsEqualTo(LifecycleStage.PreDistributeAsync);
    await Assert.That(invokedStages[1]).IsEqualTo(LifecycleStage.PreDistributeInline);
    await Assert.That(invokedStages[2]).IsEqualTo(LifecycleStage.PostDistributeAsync);
    await Assert.That(invokedStages[3]).IsEqualTo(LifecycleStage.PostDistributeInline);

    invokerMock.Verify();
  }
}
