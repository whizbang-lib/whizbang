using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Internal;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for <see cref="IReceptorFiringObserver"/> — the test-only observability hook
/// that lets integration tests deterministically await receptor firings without
/// <c>Task.Delay</c> / polling.
/// </summary>
/// <docs>operations/testing/receptor-firing-observer</docs>
public class ReceptorFiringObserverTests {

  private sealed record TestMessage(string Value) : IMessage;

  private sealed class RecordingRegistry : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void Add(ReceptorInfo info, LifecycleStage stage) {
      var key = (info.MessageType, stage);
      if (!_receptors.TryGetValue(key, out var list)) { list = []; _receptors[key] = list; }
      list.Add(info);
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      var key = (messageType, stage);
      return _receptors.TryGetValue(key, out var list) ? list : [];
    }

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  /// <summary>
  /// Minimal <see cref="IReceptorFiringObserver"/> that tracks every firing call and
  /// exposes a task per receptor that completes when that receptor's next invocation
  /// finishes. Mirrors the pattern used in PerspectiveDedupIntegrationTests for work
  /// coordination — deterministic signals, no timing-based synchronization.
  /// </summary>
  private sealed class SignallingObserver : IReceptorFiringObserver {
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _fired = [];

    public ConcurrentBag<(string ReceptorId, LifecycleStage Stage)> Firings { get; } = [];
    public ConcurrentBag<(string ReceptorId, LifecycleStage Stage, TimeSpan Duration, Exception? Exception)> Completed { get; } = [];

    public ValueTask OnReceptorFiringAsync(string receptorId, LifecycleStage stage, Guid messageId, IMessageEnvelope envelope, CancellationToken cancellationToken) {
      Firings.Add((receptorId, stage));
      return ValueTask.CompletedTask;
    }

    public ValueTask OnReceptorFiredAsync(string receptorId, LifecycleStage stage, Guid messageId, IMessageEnvelope envelope, TimeSpan duration, Exception? exception, CancellationToken cancellationToken) {
      Completed.Add((receptorId, stage, duration, exception));
      if (_fired.TryGetValue(receptorId, out var tcs)) {
        tcs.TrySetResult();
      }
      return ValueTask.CompletedTask;
    }

    public Task WaitForFiredAsync(string receptorId, TimeSpan timeout) {
      var tcs = _fired.GetOrAdd(receptorId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
      return tcs.Task.WaitAsync(timeout);
    }
  }

  private static (ReceptorInvoker Invoker, SignallingObserver Observer, ServiceProvider Provider) _buildWithObserver(
      Action<RecordingRegistry> configure) {
    var registry = new RecordingRegistry();
    configure(registry);

    var observer = new SignallingObserver();
    var services = new ServiceCollection();
    services.AddLogging(b => { b.SetMinimumLevel(LogLevel.Trace); b.AddFakeLogging(); });
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddSingleton<IReceptorDedupStore, EnvelopeReceptorDedupStore>();
    services.AddSingleton<IReceptorFiringObserver>(observer);
    services.Configure<Whizbang.Core.Configuration.WhizbangOptions>(_ => { });
    var provider = services.BuildServiceProvider();
    return (new ReceptorInvoker(registry, provider), observer, provider);
  }

  private static ReceptorInfo _stubReceptor(string id, Func<ValueTask<object?>>? body = null) => new(
    MessageType: typeof(TestMessage),
    ReceptorId: id,
    InvokeAsync: (_, _, _, _, _) => body?.Invoke() ?? ValueTask.FromResult<object?>(null)
  );

  private static MessageEnvelope<TestMessage> _envelope() => new() {
    MessageId = MessageId.From(TrackedGuid.NewMedo()),
    Payload = new TestMessage("t"),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  [Test]
  public async Task ObserverReceivesFiringAndFiredCallbacksAsync() {
    var (invoker, observer, provider) = _buildWithObserver(r => {
      r.Add(_stubReceptor("Rx"), LifecycleStage.PostInboxInline);
    });
    await using (provider) {
      await invoker.InvokeAsync(_envelope(), LifecycleStage.PostInboxInline);

      await Assert.That(observer.Firings.Count).IsEqualTo(1);
      await Assert.That(observer.Completed.Count).IsEqualTo(1);
      var (ReceptorId, Stage) = observer.Firings.First();
      await Assert.That(ReceptorId).IsEqualTo("Rx");
      await Assert.That(Stage).IsEqualTo(LifecycleStage.PostInboxInline);
    }
  }

  [Test]
  public async Task ObserverFiredCallbackCarriesExceptionOnFailureAsync() {
    var (invoker, observer, provider) = _buildWithObserver(r => {
      r.Add(_stubReceptor("Boom", () => throw new InvalidOperationException("boom")), LifecycleStage.PostInboxInline);
    });
    await using (provider) {
      await Assert.That(async () => await invoker.InvokeAsync(_envelope(), LifecycleStage.PostInboxInline))
        .Throws<InvalidOperationException>();

      await Assert.That(observer.Completed.Count).IsEqualTo(1);
      var fired = observer.Completed.First();
      await Assert.That(fired.Exception).IsNotNull();
      await Assert.That(fired.Exception!.Message).IsEqualTo("boom");
    }
  }

  [Test]
  public async Task WaitForFiredAsync_UnblocksDeterministicallyOnReceptorCompletionAsync() {
    // Demonstrates the primary use case: tests await `WaitForFiredAsync` instead of polling
    // or sleeping. The invocation drives a real TaskCompletionSource completion.
    var (invoker, observer, provider) = _buildWithObserver(r => {
      r.Add(_stubReceptor("Target"), LifecycleStage.PostInboxInline);
    });
    await using (provider) {
      var waitTask = observer.WaitForFiredAsync("Target", TimeSpan.FromSeconds(5));

      await invoker.InvokeAsync(_envelope(), LifecycleStage.PostInboxInline);

      await waitTask; // completes deterministically once the receptor's finally ran
      // If we reach this line, the observer fired and the test is green — no timeout path needed.
    }
  }

  [Test]
  public async Task ObserverNotCalledWhenGuardrailSkipsInvocationAsync() {
    // When the dedup guardrail blocks a duplicate fire attempt, the observer should NOT
    // see a Firing/Fired pair — the skip is a pre-invocation decision.
    var (invoker, observer, provider) = _buildWithObserver(r => {
      r.Add(_stubReceptor("Rx"), LifecycleStage.PostInboxInline);
    });
    await using (provider) {
      var envelope = _envelope();
      envelope.ReceptorInvocations = [
        new ReceptorInvocationRecord {
          ReceptorId = "Rx",
          Stage = LifecycleStage.LocalImmediateInline,
          CompletedAt = DateTimeOffset.UtcNow,
          Duration = TimeSpan.Zero,
          ServiceName = "prior"
        }
      ];

      await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

      // Guardrail skipped — no fires observed.
      await Assert.That(observer.Firings.Count).IsEqualTo(0);
      await Assert.That(observer.Completed.Count).IsEqualTo(0);
    }
  }
}
