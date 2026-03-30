using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Dispatch;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for receptor suppression during replay and rebuild operations.
/// Verifies that receptors without [FireDuringReplay] are suppressed when ProcessingMode is Replay or Rebuild,
/// and that opted-in receptors (with FireDuringReplay = true) still fire with correct ProcessingMode on context.
/// </summary>
/// <tests>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</tests>
public class ReceptorSuppressionTests {

  private sealed record TestMessage(string Value) : IMessage;

  private static ServiceProvider _createServiceProvider() {
    var services = new ServiceCollection();
    return services.BuildServiceProvider();
  }

  private static MessageEnvelope<T> _wrapInEnvelope<T>(T message) where T : notnull {
    return new MessageEnvelope<T> {
      MessageId = MessageId.From(TrackedGuid.NewMedo()),
      Payload = message,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  /// <summary>
  /// Tracks invocations with their context for verification.
  /// </summary>
  private sealed class InvocationTracker {
    public List<(string ReceptorId, ProcessingMode? Mode)> Invocations { get; } = [];
    public int InvocationCount => Invocations.Count;

    public void RecordInvocation(string receptorId, ProcessingMode? mode) =>
        Invocations.Add((receptorId, mode));
  }

  /// <summary>
  /// Test registry that supports FireDuringReplay metadata.
  /// </summary>
  private sealed class TestReceptorRegistry(InvocationTracker tracker) : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void RegisterReceptor<TMessage>(
        string receptorId, LifecycleStage stage, bool fireDuringReplay = false) {
      var key = (typeof(TMessage), stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }

      list.Add(new ReceptorInfo(
        MessageType: typeof(TMessage),
        ReceptorId: receptorId,
        InvokeAsync: (_, _, _, _, _) => {
          tracker.RecordInvocation(receptorId, null);
          return ValueTask.FromResult<object?>(null);
        },
        FireDuringReplay: fireDuringReplay
      ));
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      var key = (messageType, stage);
      return _receptors.TryGetValue(key, out var list) ? list : [];
    }

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => throw new NotImplementedException();
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => throw new NotImplementedException();
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => throw new NotImplementedException();
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => throw new NotImplementedException();
  }

  #region Suppression During Replay

  [Test]
  public async Task InvokeAsync_ReplayMode_SuppressesReceptorWithoutFireDuringReplayAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("NormalReceptor", LifecycleStage.PostPerspectiveInline, fireDuringReplay: false);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Replay
    };

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(new TestMessage("test")), LifecycleStage.PostPerspectiveInline, context);

    // Assert — receptor should NOT have fired
    await Assert.That(tracker.InvocationCount).IsEqualTo(0);
  }

  [Test]
  public async Task InvokeAsync_ReplayMode_AllowsReceptorWithFireDuringReplayAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("OptedInReceptor", LifecycleStage.PostPerspectiveInline, fireDuringReplay: true);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Replay
    };

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(new TestMessage("test")), LifecycleStage.PostPerspectiveInline, context);

    // Assert — opted-in receptor should fire
    await Assert.That(tracker.InvocationCount).IsEqualTo(1);
    await Assert.That(tracker.Invocations[0].ReceptorId).IsEqualTo("OptedInReceptor");
  }

  #endregion

  #region Suppression During Rebuild

  [Test]
  public async Task InvokeAsync_RebuildMode_SuppressesReceptorWithoutFireDuringReplayAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("NormalReceptor", LifecycleStage.PostPerspectiveInline, fireDuringReplay: false);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Rebuild
    };

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(new TestMessage("test")), LifecycleStage.PostPerspectiveInline, context);

    // Assert — receptor should NOT have fired
    await Assert.That(tracker.InvocationCount).IsEqualTo(0);
  }

  [Test]
  public async Task InvokeAsync_RebuildMode_AllowsReceptorWithFireDuringReplayAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("OptedInReceptor", LifecycleStage.PostPerspectiveInline, fireDuringReplay: true);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Rebuild
    };

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(new TestMessage("test")), LifecycleStage.PostPerspectiveInline, context);

    // Assert — opted-in receptor should fire
    await Assert.That(tracker.InvocationCount).IsEqualTo(1);
  }

  #endregion

  #region Live Mode — No Suppression

  [Test]
  public async Task InvokeAsync_LiveMode_AllReceptorsFireAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("NormalReceptor", LifecycleStage.PostPerspectiveInline, fireDuringReplay: false);
    registry.RegisterReceptor<TestMessage>("OptedInReceptor", LifecycleStage.PostPerspectiveInline, fireDuringReplay: true);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Live
    };

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(new TestMessage("test")), LifecycleStage.PostPerspectiveInline, context);

    // Assert — BOTH receptors should fire during live mode
    await Assert.That(tracker.InvocationCount).IsEqualTo(2);
  }

  [Test]
  public async Task InvokeAsync_NullProcessingMode_AllReceptorsFireAsync() {
    // Arrange — backward compat: null ProcessingMode means live
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("NormalReceptor", LifecycleStage.PostPerspectiveInline, fireDuringReplay: false);
    registry.RegisterReceptor<TestMessage>("OptedInReceptor", LifecycleStage.PostPerspectiveInline, fireDuringReplay: true);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    // context is null — no ProcessingMode
    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(new TestMessage("test")), LifecycleStage.PostPerspectiveInline);

    // Assert — BOTH receptors should fire when no context
    await Assert.That(tracker.InvocationCount).IsEqualTo(2);
  }

  [Test]
  public async Task InvokeAsync_NullContext_AllReceptorsFireAsync() {
    // Arrange
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("NormalReceptor", LifecycleStage.PostPerspectiveInline, fireDuringReplay: false);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());

    // Act — null context means live processing
    await invoker.InvokeAsync(_wrapInEnvelope(new TestMessage("test")), LifecycleStage.PostPerspectiveInline, context: null);

    // Assert — receptor should fire
    await Assert.That(tracker.InvocationCount).IsEqualTo(1);
  }

  #endregion

  #region Mixed Receptors

  [Test]
  public async Task InvokeAsync_ReplayMode_MixedReceptors_OnlyOptedInFiresAsync() {
    // Arrange — one receptor with FireDuringReplay, one without
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("EmailReceptor", LifecycleStage.PostPerspectiveInline, fireDuringReplay: false);
    registry.RegisterReceptor<TestMessage>("DependentModelUpdater", LifecycleStage.PostPerspectiveInline, fireDuringReplay: true);
    registry.RegisterReceptor<TestMessage>("WebhookReceptor", LifecycleStage.PostPerspectiveInline, fireDuringReplay: false);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Replay
    };

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(new TestMessage("test")), LifecycleStage.PostPerspectiveInline, context);

    // Assert — only DependentModelUpdater should fire
    await Assert.That(tracker.InvocationCount).IsEqualTo(1);
    await Assert.That(tracker.Invocations[0].ReceptorId).IsEqualTo("DependentModelUpdater");
  }

  [Test]
  public async Task InvokeAsync_RebuildMode_AllSuppressed_ReturnsEarlyAsync() {
    // Arrange — all receptors without FireDuringReplay
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("Receptor1", LifecycleStage.PostPerspectiveInline, fireDuringReplay: false);
    registry.RegisterReceptor<TestMessage>("Receptor2", LifecycleStage.PostPerspectiveInline, fireDuringReplay: false);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Rebuild
    };

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(new TestMessage("test")), LifecycleStage.PostPerspectiveInline, context);

    // Assert — no receptors should fire
    await Assert.That(tracker.InvocationCount).IsEqualTo(0);
  }

  [Test]
  public async Task InvokeAsync_ReplayMode_MultipleOptedIn_AllFireAsync() {
    // Arrange — multiple receptors with FireDuringReplay
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("Updater1", LifecycleStage.PostPerspectiveInline, fireDuringReplay: true);
    registry.RegisterReceptor<TestMessage>("Updater2", LifecycleStage.PostPerspectiveInline, fireDuringReplay: true);
    registry.RegisterReceptor<TestMessage>("Suppressed", LifecycleStage.PostPerspectiveInline, fireDuringReplay: false);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Replay
    };

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(new TestMessage("test")), LifecycleStage.PostPerspectiveInline, context);

    // Assert — both opted-in receptors fire, suppressed one doesn't
    await Assert.That(tracker.InvocationCount).IsEqualTo(2);
  }

  #endregion

  #region ProcessingMode on Context — Opted-In Receptor Receives Correct Mode

  [Test]
  public async Task InvokeAsync_ReplayMode_OptedInReceptor_ReceivesReplayContextAsync() {
    // Arrange — verify ProcessingMode is accessible on context passed to opted-in receptor
    ProcessingMode? capturedMode = null;
    var registry = new CaptureContextRegistry(mode => capturedMode = mode);

    var sp = _createServiceProviderWithLifecycleContextAccessor();
    var invoker = new ReceptorInvoker(registry, sp);
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Replay
    };

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(new TestMessage("test")), LifecycleStage.PostPerspectiveInline, context);

    // Assert — context.ProcessingMode was Replay when set on accessor
    await Assert.That(capturedMode).IsEqualTo(ProcessingMode.Replay);
  }

  [Test]
  public async Task InvokeAsync_RebuildMode_OptedInReceptor_ReceivesRebuildContextAsync() {
    // Arrange
    ProcessingMode? capturedMode = null;
    var registry = new CaptureContextRegistry(mode => capturedMode = mode);

    var sp = _createServiceProviderWithLifecycleContextAccessor();
    var invoker = new ReceptorInvoker(registry, sp);
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Rebuild
    };

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(new TestMessage("test")), LifecycleStage.PostPerspectiveInline, context);

    // Assert
    await Assert.That(capturedMode).IsEqualTo(ProcessingMode.Rebuild);
  }

  private static ServiceProvider _createServiceProviderWithLifecycleContextAccessor() {
    var services = new ServiceCollection();
    services.AddSingleton<ILifecycleContextAccessor>(new AsyncLocalLifecycleContextAccessor());
    return services.BuildServiceProvider();
  }

  /// <summary>
  /// Registry that captures the ProcessingMode from the lifecycle context accessor during invocation.
  /// The receptor has FireDuringReplay = true so it fires during replay/rebuild.
  /// </summary>
  private sealed class CaptureContextRegistry(Action<ProcessingMode?> captureMode) : IReceptorRegistry {
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      if (messageType == typeof(TestMessage) && stage == LifecycleStage.PostPerspectiveInline) {
        return [new ReceptorInfo(
          MessageType: typeof(TestMessage),
          ReceptorId: "ContextCapture",
          InvokeAsync: (sp, _, _, _, _) => {
            var accessor = sp.GetService<ILifecycleContextAccessor>();
            captureMode(accessor?.Current?.ProcessingMode);
            return ValueTask.FromResult<object?>(null);
          },
          FireDuringReplay: true
        )];
      }
      return [];
    }

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => throw new NotImplementedException();
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => throw new NotImplementedException();
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => throw new NotImplementedException();
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => throw new NotImplementedException();
  }

  #endregion

  #region Edge Cases

  [Test]
  public async Task InvokeAsync_ReplayMode_NoReceptorsRegistered_DoesNotThrowAsync() {
    // Arrange — empty registry
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Replay
    };

    // Act & Assert — should complete without error
    await invoker.InvokeAsync(_wrapInEnvelope(new TestMessage("test")), LifecycleStage.PostPerspectiveInline, context);
    await Assert.That(tracker.InvocationCount).IsEqualTo(0);
  }

  [Test]
  public async Task InvokeAsync_LiveMode_ExistingReceptorsWithoutAttribute_ContinueToWorkAsync() {
    // Arrange — backward compatibility: receptors without FireDuringReplay work in Live mode
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("LegacyReceptor", LifecycleStage.PostPerspectiveInline, fireDuringReplay: false);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());

    // Act — no context (live processing)
    await invoker.InvokeAsync(_wrapInEnvelope(new TestMessage("test")), LifecycleStage.PostPerspectiveInline);

    // Assert — should fire normally
    await Assert.That(tracker.InvocationCount).IsEqualTo(1);
  }

  [Test]
  public async Task InvokeAsync_ContextWithNullProcessingMode_DoesNotSuppressAsync() {
    // Arrange — ProcessingMode is explicitly null
    var tracker = new InvocationTracker();
    var registry = new TestReceptorRegistry(tracker);
    registry.RegisterReceptor<TestMessage>("Receptor", LifecycleStage.PostPerspectiveInline, fireDuringReplay: false);

    var invoker = new ReceptorInvoker(registry, _createServiceProvider());
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = null
    };

    // Act
    await invoker.InvokeAsync(_wrapInEnvelope(new TestMessage("test")), LifecycleStage.PostPerspectiveInline, context);

    // Assert — null ProcessingMode means live, receptor should fire
    await Assert.That(tracker.InvocationCount).IsEqualTo(1);
  }

  #endregion
}
