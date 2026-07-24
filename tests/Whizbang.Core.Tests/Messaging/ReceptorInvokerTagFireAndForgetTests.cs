using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Pins the fire-and-forget contract for <see cref="ReceptorInvoker._processTagsAsync"/>.
/// Tag handlers (signaltags, UI notification pushes, metrics) must not block pipeline
/// throughput — slow tag I/O must not gate the perspective worker's next cycle.
/// <tests>src/Whizbang.Core/Messaging/ReceptorInvoker.cs:_processTagsAsync</tests>
/// </summary>
public class ReceptorInvokerTagFireAndForgetTests {

  private sealed record PingMessage(Guid Id) : IMessage;

  /// <summary>Tag processor that signals when it starts and blocks until an external gate is released.</summary>
  private sealed class BlockingTagProcessor(TaskCompletionSource started, TaskCompletionSource gate) : IMessageTagProcessor {
    public async ValueTask ProcessTagsAsync(
        object message,
        Type messageType,
        LifecycleStage stage,
        IScopeContext? scope = null,
        CancellationToken ct = default) {
      started.TrySetResult();
      await gate.Task.WaitAsync(ct);
    }
  }

  private sealed class EmptyReceptorRegistry : IReceptorRegistry {
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) =>
      [];
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  private static MessageEnvelope<IMessage> _createEnvelope(IMessage payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [new MessageHop {
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      ServiceInstance = new ServiceInstanceInfo { InstanceId = Guid.NewGuid(), ServiceName = "Test", HostName = "test", ProcessId = 1 }
    }],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  [Test]
  public async Task InvokeAsync_WithBlockingTagHandler_ReturnsBeforeHandlerCompletesAsync() {
    // Arrange — tag processor blocks on a gate. If tag processing were awaited,
    // InvokeAsync would block too; with fire-and-forget it must return promptly.
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var blockingProcessor = new BlockingTagProcessor(started, gate);

    var services = new ServiceCollection();
    services.AddSingleton<IMessageTagProcessor>(blockingProcessor);
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    var invoker = new ReceptorInvoker(new EmptyReceptorRegistry(), sp);
    var envelope = _createEnvelope(new PingMessage(Guid.NewGuid()));

    // Act — InvokeAsync should return even though the tag handler is still blocked
    await invoker.InvokeAsync(envelope, LifecycleStage.PostLifecycleInline).AsTask()
      .WaitAsync(TimeSpan.FromSeconds(5));

    // Wait for the tag handler to begin (proves it was scheduled, not skipped)
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Assert — tag handler is still blocked; InvokeAsync already returned
    await Assert.That(gate.Task.IsCompleted).IsFalse()
      .Because("Gate is closed — the handler is genuinely blocked and would have blocked InvokeAsync if tags were still awaited");
    await Assert.That(started.Task.IsCompletedSuccessfully).IsTrue()
      .Because("The tag handler was scheduled (and started executing) before InvokeAsync returned");

    // Cleanup — release the gate so the background task completes
    gate.TrySetResult();
  }

  [Test]
  public async Task InvokeAsync_TagHandlerThrows_DoesNotPropagateToCallerAsync() {
    // Arrange — tag processor that throws; caller must not see the exception
    var services = new ServiceCollection();
    services.AddSingleton<IMessageTagProcessor>(new ThrowingTagProcessor());
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    var invoker = new ReceptorInvoker(new EmptyReceptorRegistry(), sp);
    var envelope = _createEnvelope(new PingMessage(Guid.NewGuid()));

    // Act — exception inside tag handler must be observed (logged), not thrown back.
    // If the exception propagated, WaitAsync would rethrow it and this test would fail.
    var invokeTask = invoker.InvokeAsync(envelope, LifecycleStage.PostLifecycleInline).AsTask();
    await invokeTask.WaitAsync(TimeSpan.FromSeconds(5));

    // Assert — InvokeAsync completed successfully despite the tag handler throwing
    await Assert.That(invokeTask.IsCompletedSuccessfully).IsTrue()
      .Because("A throwing tag handler must be observed+logged on the background task, not propagated to InvokeAsync");
  }

  private sealed class ThrowingTagProcessor : IMessageTagProcessor {
    public ValueTask ProcessTagsAsync(
        object message,
        Type messageType,
        LifecycleStage stage,
        IScopeContext? scope = null,
        CancellationToken ct = default) {
      return ValueTask.FromException(new InvalidOperationException("tag handler boom"));
    }
  }
}
