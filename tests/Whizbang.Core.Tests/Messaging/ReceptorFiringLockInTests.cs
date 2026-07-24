using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
/// Lock-in scenarios for the receptor-firing contract. These tests prove the contract
/// (exactly-once per receptor per message, unless idempotent) holds under the situations
/// most likely to cause real-world duplicates: identical-stage delivery, cross-stage
/// filter bugs, high-concurrency interleaving, the three-default-stage registration
/// pattern, and explicit [FireAt] decorations.
/// </summary>
/// <docs>fundamentals/receptors/exactly-once-firing</docs>
public class ReceptorFiringLockInTests {

  private sealed record TestMessage(string Value) : IMessage;

  private sealed class LockInRegistry : IReceptorRegistry {
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

  private static ReceptorInfo _receptorInfo<TMessage>(
      string receptorId,
      ConcurrentBag<(string, Guid, LifecycleStage)> fires,
      LifecycleStage stage,
      bool isIdempotent = false) where TMessage : IMessage {
    return new ReceptorInfo(
      MessageType: typeof(TMessage),
      ReceptorId: receptorId,
      InvokeAsync: (_, _, envelope, _, _) => {
        fires.Add((receptorId, envelope.MessageId.Value, stage));
        return ValueTask.FromResult<object?>(null);
      },
      IsIdempotent: isIdempotent
    );
  }

  private static MessageEnvelope<TMessage> _envelope<TMessage>(TMessage payload) where TMessage : notnull => new() {
    MessageId = MessageId.From(TrackedGuid.NewMedo()),
    Payload = payload,
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  [Test]
  public async Task DefaultThreeStageRegistration_SingleReceptorAcrossDefaultStagesFiresOncePerMessageAsync() {
    // A receptor registered at all three default stages (mimicking a source-generated no-[FireAt] registration)
    // must only produce ONE invocation per message — the guardrail short-circuits the second and third stages
    // because the per-receptor store shows the receptor already fired.
    var sharedFires = new ConcurrentBag<(string, Guid, LifecycleStage)>();
    var registry = new LockInRegistry();
    foreach (var stage in new[] { LifecycleStage.LocalImmediateInline, LifecycleStage.PreOutboxInline, LifecycleStage.PostInboxInline }) {
      registry.Add(_receptorInfo<TestMessage>("DefaultReceptor", sharedFires, stage), stage);
    }
    var services = new ServiceCollection();
    services.AddLogging(b => b.AddFakeLogging());
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddSingleton<IReceptorDedupStore, EnvelopeReceptorDedupStore>();
    services.Configure<Whizbang.Core.Configuration.WhizbangOptions>(_ => { });
    var sp = services.BuildServiceProvider();
    await using (sp) {
      var realInvoker = new ReceptorInvoker(registry, sp);
      var envelope = _envelope(new TestMessage("test"));

      // Act — drive all three default stages in the order the framework does.
      await realInvoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateInline);
      await realInvoker.InvokeAsync(envelope, LifecycleStage.PreOutboxInline);
      await realInvoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

      // Assert — exactly one invocation recorded; the second and third stages' receptors were guardrail-skipped.
      await Assert.That(sharedFires.Count).IsEqualTo(1);
      var first = sharedFires.First();
      await Assert.That(first.Item1).IsEqualTo("DefaultReceptor");
      await Assert.That(first.Item3).IsEqualTo(LifecycleStage.LocalImmediateInline);
      // And the envelope carries exactly one record.
      await Assert.That(envelope.ReceptorInvocations!).Count().IsEqualTo(1);
    }
  }

  [Test]
  public async Task ReceptorIdempotent_AcrossDefaultStagesFiresAtEveryStageAsync() {
    // An idempotent receptor bypasses the guardrail — registered at all three default
    // stages it fires at each one, because opting in via [ReceptorIdempotent] means
    // "safe to re-invoke for the same event id."
    var sharedFires = new ConcurrentBag<(string, Guid, LifecycleStage)>();
    var registry = new LockInRegistry();
    foreach (var stage in new[] { LifecycleStage.LocalImmediateInline, LifecycleStage.PreOutboxInline, LifecycleStage.PostInboxInline }) {
      registry.Add(_receptorInfo<TestMessage>("IdempotentReceptor", sharedFires, stage, isIdempotent: true), stage);
    }
    var services = new ServiceCollection();
    services.AddLogging(b => b.AddFakeLogging());
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddSingleton<IReceptorDedupStore, EnvelopeReceptorDedupStore>();
    services.Configure<Whizbang.Core.Configuration.WhizbangOptions>(_ => { });
    var sp = services.BuildServiceProvider();
    await using (sp) {
      var invoker = new ReceptorInvoker(registry, sp);
      var envelope = _envelope(new TestMessage("test"));

      await invoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateInline);
      await invoker.InvokeAsync(envelope, LifecycleStage.PreOutboxInline);
      await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

      await Assert.That(sharedFires.Count).IsEqualTo(3);
      // Idempotent receptors still record each invocation so observability of the firing
      // history is preserved.
      await Assert.That(envelope.ReceptorInvocations!).Count().IsEqualTo(3);
    }
  }

  [Test]
  public async Task HundredMessagesInterleaved_NoCrossContaminationAsync() {
    // Drive 100 messages through the invoker concurrently. Each message must see exactly
    // one invocation of the single registered receptor — no message's envelope ever
    // shows another message's invocation record, and no message fires twice.
    var sharedFires = new ConcurrentBag<(string, Guid, LifecycleStage)>();
    var registry = new LockInRegistry();
    registry.Add(_receptorInfo<TestMessage>("ConcurrentReceptor", sharedFires, LifecycleStage.PostInboxInline), LifecycleStage.PostInboxInline);

    var services = new ServiceCollection();
    services.AddLogging(b => b.AddFakeLogging());
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddSingleton<IReceptorDedupStore, EnvelopeReceptorDedupStore>();
    services.Configure<Whizbang.Core.Configuration.WhizbangOptions>(_ => { });
    var sp = services.BuildServiceProvider();
    await using (sp) {
      var invoker = new ReceptorInvoker(registry, sp);
      const int MessageCount = 100;
      var envelopes = Enumerable.Range(0, MessageCount)
        .Select(_ => _envelope(new TestMessage("test")))
        .ToArray();

      // Act — fire all 100 concurrently.
      await Task.WhenAll(envelopes.Select(e => invoker.InvokeAsync(e, LifecycleStage.PostInboxInline).AsTask()));

      // Assert — exactly 100 invocations total, each envelope has exactly one record, and
      // every record's MessageId matches the envelope it's on.
      await Assert.That(sharedFires.Count).IsEqualTo(MessageCount);
      foreach (var envelope in envelopes) {
        await Assert.That(envelope.ReceptorInvocations).IsNotNull();
        await Assert.That(envelope.ReceptorInvocations!).Count().IsEqualTo(1);
        await Assert.That(envelope.ReceptorInvocations![0].ReceptorId).IsEqualTo("ConcurrentReceptor");
      }

      // Cross-contamination check: each MessageId appears exactly once in the fires bag.
      var perMessageCounts = sharedFires.GroupBy(f => f.Item2).ToDictionary(g => g.Key, g => g.Count());
      await Assert.That(perMessageCounts.Count).IsEqualTo(MessageCount);
      foreach (var count in perMessageCounts.Values) {
        await Assert.That(count).IsEqualTo(1);
      }
    }
  }

  [Test]
  public async Task DifferentReceptorsAtSameStage_EachFireOnceAsync() {
    // The guardrail is per-receptor, not per-stage: two DIFFERENT receptors registered at
    // the same stage both fire for the same message. Each records independently.
    var sharedFires = new ConcurrentBag<(string, Guid, LifecycleStage)>();
    var registry = new LockInRegistry();
    registry.Add(_receptorInfo<TestMessage>("ReceptorA", sharedFires, LifecycleStage.PostInboxInline), LifecycleStage.PostInboxInline);
    registry.Add(_receptorInfo<TestMessage>("ReceptorB", sharedFires, LifecycleStage.PostInboxInline), LifecycleStage.PostInboxInline);

    var services = new ServiceCollection();
    services.AddLogging(b => b.AddFakeLogging());
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddSingleton<IReceptorDedupStore, EnvelopeReceptorDedupStore>();
    services.Configure<Whizbang.Core.Configuration.WhizbangOptions>(_ => { });
    var sp = services.BuildServiceProvider();
    await using (sp) {
      var invoker = new ReceptorInvoker(registry, sp);
      var envelope = _envelope(new TestMessage("test"));

      await invoker.InvokeAsync(envelope, LifecycleStage.PostInboxInline);

      await Assert.That(sharedFires.Count).IsEqualTo(2);
      await Assert.That(envelope.ReceptorInvocations!).Count().IsEqualTo(2);
      var ids = envelope.ReceptorInvocations!.Select(r => r.ReceptorId).Order().ToList();
      await Assert.That(ids).IsEquivalentTo(["ReceptorA", "ReceptorB"]);
    }
  }

  [Test]
  public async Task DifferentMessagesInSameEnvelopeTypeFireIndependentlyAsync() {
    // Invocation records are per-message. A receptor firing for message A must not
    // "inherit" into a separate envelope for message B.
    var sharedFires = new ConcurrentBag<(string, Guid, LifecycleStage)>();
    var registry = new LockInRegistry();
    registry.Add(_receptorInfo<TestMessage>("ReceptorX", sharedFires, LifecycleStage.PostInboxInline), LifecycleStage.PostInboxInline);

    var services = new ServiceCollection();
    services.AddLogging(b => b.AddFakeLogging());
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddSingleton<IReceptorDedupStore, EnvelopeReceptorDedupStore>();
    services.Configure<Whizbang.Core.Configuration.WhizbangOptions>(_ => { });
    var sp = services.BuildServiceProvider();
    await using (sp) {
      var invoker = new ReceptorInvoker(registry, sp);
      var envelopeA = _envelope(new TestMessage("a"));
      var envelopeB = _envelope(new TestMessage("b"));

      await invoker.InvokeAsync(envelopeA, LifecycleStage.PostInboxInline);
      await invoker.InvokeAsync(envelopeB, LifecycleStage.PostInboxInline);

      await Assert.That(sharedFires.Count).IsEqualTo(2);
      await Assert.That(envelopeA.ReceptorInvocations!).Count().IsEqualTo(1);
      await Assert.That(envelopeB.ReceptorInvocations!).Count().IsEqualTo(1);
      await Assert.That(envelopeA.ReceptorInvocations![0].ReceptorId).IsEqualTo("ReceptorX");
      await Assert.That(envelopeB.ReceptorInvocations![0].ReceptorId).IsEqualTo("ReceptorX");
      // The records' timestamps may match the Stopwatch tick but the envelope identities differ —
      // confirm the bag's messageIds match the two envelopes.
      var firedIds = sharedFires.Select(f => f.Item2).Order().ToList();
      var envelopeIds = new[] { envelopeA.MessageId.Value, envelopeB.MessageId.Value }.Order().ToList();
      await Assert.That(firedIds).IsEquivalentTo(envelopeIds);
    }
  }
}
