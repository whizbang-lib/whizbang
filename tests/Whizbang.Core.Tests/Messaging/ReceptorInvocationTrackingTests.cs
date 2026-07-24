using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Internal;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// End-to-end behaviour tests for the double-fire guardrail in
/// <see cref="ReceptorInvoker"/>: records invocations on success, skips (or throws) on a
/// second attempt for the same receptor, bypasses for <c>[ReceptorIdempotent]</c>,
/// and does not record on exception.
/// </summary>
/// <docs>fundamentals/receptors/exactly-once-firing</docs>
public class ReceptorInvocationTrackingTests {

  private sealed record TestMessage(string Value) : IMessage;

  private sealed class TrackingInvocationRegistry : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void Add(ReceptorInfo info, LifecycleStage stage) {
      var key = (info.MessageType, stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }
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

  private static (ReceptorInvoker Invoker, ServiceProvider Provider, FakeLogCollector Collector, List<string> Fires) _buildInvoker(
      string receptorId,
      LifecycleStage stage,
      bool isIdempotent = false,
      Action<WhizbangGuardrailsOptions>? configureGuardrails = null,
      Func<ValueTask<object?>>? customBody = null) {
    var fires = new List<string>();
    var registry = new TrackingInvocationRegistry();
    registry.Add(new ReceptorInfo(
      MessageType: typeof(TestMessage),
      ReceptorId: receptorId,
      InvokeAsync: async (_, _, _, _, _) => {
        fires.Add(receptorId);
        if (customBody is not null) {
          return await customBody().ConfigureAwait(false);
        }
        return null;
      },
      IsIdempotent: isIdempotent
    ), stage);

    var services = new ServiceCollection();
    services.AddLogging(b => {
      b.SetMinimumLevel(LogLevel.Trace);
      b.AddFakeLogging();
    });
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddSingleton<IReceptorDedupStore, EnvelopeReceptorDedupStore>();
    services.Configure<WhizbangOptions>(o => {
      configureGuardrails?.Invoke(o.Guardrails);
    });
    var provider = services.BuildServiceProvider();
    var collector = provider.GetFakeLogCollector();

    var invoker = new ReceptorInvoker(registry, provider);
    return (invoker, provider, collector, fires);
  }

  private static MessageEnvelope<TestMessage> _envelope() => new() {
    MessageId = MessageId.From(TrackedGuid.NewMedo()),
    Payload = new TestMessage("test"),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  [Test]
  public async Task RecordsSingleInvocationPerReceptorOnSuccessAsync() {
    var (invoker, provider, _, fires) = _buildInvoker("MyReceptor", LifecycleStage.PostInboxInline);
    await using (provider) {
      var envelope = _envelope();

      await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

      await Assert.That(fires).Count().IsEqualTo(1);
      await Assert.That(envelope.ReceptorInvocations).IsNotNull();
      await Assert.That(envelope.ReceptorInvocations!).Count().IsEqualTo(1);
      await Assert.That(envelope.ReceptorInvocations![0].ReceptorId).IsEqualTo("MyReceptor");
      await Assert.That(envelope.ReceptorInvocations[0].Stage).IsEqualTo(LifecycleStage.PostInboxInline);
    }
  }

  [Test]
  public async Task SkipsAndWarnsWhenReceptorAlreadyFiredSameStageAsync() {
    var (invoker, provider, collector, fires) = _buildInvoker("MyReceptor", LifecycleStage.PostInboxInline);
    await using (provider) {
      var envelope = _envelope();
      envelope.ReceptorInvocations = [
        new ReceptorInvocationRecord {
          ReceptorId = "MyReceptor",
          Stage = LifecycleStage.PostInboxInline,
          CompletedAt = DateTimeOffset.UtcNow.AddMilliseconds(-50),
          Duration = TimeSpan.FromMilliseconds(5),
          ServiceName = "test-service"
        }
      ];

      await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

      await Assert.That(fires).Count().IsEqualTo(0);
      var warn = collector.GetSnapshot().FirstOrDefault(r => r.Level == LogLevel.Warning && r.Id.Name == "ReceptorAlreadyFiredSkip");
      await Assert.That(warn).IsNotNull();
    }
  }

  [Test]
  public async Task SkipsAndWarnsWhenReceptorAlreadyFiredPriorStageAsync() {
    // Cross-stage case: receptor fired at LocalImmediateInline, now trying to fire at PreOutboxInline.
    // The guardrail is per-receptor, not per-stage — this is the key filter-bug detector.
    var (invoker, provider, collector, fires) = _buildInvoker("MyReceptor", LifecycleStage.PreOutboxInline);
    await using (provider) {
      var envelope = _envelope();
      envelope.ReceptorInvocations = [
        new ReceptorInvocationRecord {
          ReceptorId = "MyReceptor",
          Stage = LifecycleStage.LocalImmediateInline,
          CompletedAt = DateTimeOffset.UtcNow.AddMilliseconds(-50),
          Duration = TimeSpan.FromMilliseconds(5),
          ServiceName = "test-service"
        }
      ];

      await invoker.InvokeAsync(envelope, LifecycleStage.PreOutboxInline);

      await Assert.That(fires).Count().IsEqualTo(0);
      var warn = collector.GetSnapshot().FirstOrDefault(r => r.Level == LogLevel.Warning && r.Id.Name == "ReceptorAlreadyFiredSkip");
      await Assert.That(warn).IsNotNull();
      var state = warn!.StructuredState!.ToDictionary(p => p.Key, p => p.Value);
      await Assert.That(state["CurrentStage"]).IsEqualTo(nameof(LifecycleStage.PreOutboxInline));
      await Assert.That(state["PriorStage"]).IsEqualTo(nameof(LifecycleStage.LocalImmediateInline));
    }
  }

  [Test]
  public async Task ReceptorIdempotentBypassesGuardAsync() {
    var (invoker, provider, _, fires) = _buildInvoker(
      "MyIdempotentReceptor",
      LifecycleStage.PostInboxInline,
      isIdempotent: true);
    await using (provider) {
      var envelope = _envelope();
      envelope.ReceptorInvocations = [
        new ReceptorInvocationRecord {
          ReceptorId = "MyIdempotentReceptor",
          Stage = LifecycleStage.LocalImmediateInline,
          CompletedAt = DateTimeOffset.UtcNow,
          Duration = TimeSpan.Zero,
          ServiceName = "test-service"
        }
      ];

      await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

      await Assert.That(fires).Count().IsEqualTo(1);
    }
  }

  [Test]
  public async Task DoesNotRecordInvocationWhenReceptorThrowsAsync() {
    var (invoker, provider, _, _) = _buildInvoker(
      "ThrowingReceptor",
      LifecycleStage.PostInboxInline,
      customBody: () => throw new InvalidOperationException("boom"));
    await using (provider) {
      var envelope = _envelope();

      await Assert.That(async () => await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline))
        .Throws<InvalidOperationException>();

      // Crucial: no record, so a retry can still fire.
      await Assert.That(envelope.ReceptorInvocations is null || envelope.ReceptorInvocations.Count == 0).IsTrue();
    }
  }

  [Test]
  public async Task TrackOnlyModeRecordsButDoesNotEnforceAsync() {
    var (invoker, provider, _, fires) = _buildInvoker(
      "MyReceptor",
      LifecycleStage.PostInboxInline,
      configureGuardrails: g => g.ReceptorInvocationTracking = ReceptorInvocationTracking.Track);
    await using (provider) {
      var envelope = _envelope();
      envelope.ReceptorInvocations = [
        new ReceptorInvocationRecord {
          ReceptorId = "MyReceptor",
          Stage = LifecycleStage.LocalImmediateInline,
          CompletedAt = DateTimeOffset.UtcNow,
          Duration = TimeSpan.Zero,
          ServiceName = "test-service"
        }
      ];

      await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

      // Track mode: the receptor STILL fires even though a prior invocation exists,
      // but the new invocation is appended to the list so the data is available for
      // observability / later rollout of enforcement.
      await Assert.That(fires).Count().IsEqualTo(1);
      await Assert.That(envelope.ReceptorInvocations!).Count().IsEqualTo(2);
    }
  }

  [Test]
  public async Task OffModeDoesNotRecordOrEnforceAsync() {
    var (invoker, provider, _, fires) = _buildInvoker(
      "MyReceptor",
      LifecycleStage.PostInboxInline,
      configureGuardrails: g => g.ReceptorInvocationTracking = ReceptorInvocationTracking.Off);
    await using (provider) {
      var envelope = _envelope();

      await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

      await Assert.That(fires).Count().IsEqualTo(1);
      await Assert.That(envelope.ReceptorInvocations is null || envelope.ReceptorInvocations.Count == 0).IsTrue();
    }
  }

  [Test]
  public async Task OnDoubleFireThrowRaisesDuplicateReceptorFireExceptionAsync() {
    var (invoker, provider, _, fires) = _buildInvoker(
      "MyReceptor",
      LifecycleStage.PostInboxInline,
      configureGuardrails: g => g.OnDoubleFire = DoubleFireBehavior.Throw);
    await using (provider) {
      var envelope = _envelope();
      envelope.ReceptorInvocations = [
        new ReceptorInvocationRecord {
          ReceptorId = "MyReceptor",
          Stage = LifecycleStage.LocalImmediateInline,
          CompletedAt = DateTimeOffset.UtcNow,
          Duration = TimeSpan.Zero,
          ServiceName = "test-service"
        }
      ];

      await Assert.That(async () => await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline))
        .Throws<DuplicateReceptorFireException>();
      await Assert.That(fires).Count().IsEqualTo(0);
    }
  }
}
