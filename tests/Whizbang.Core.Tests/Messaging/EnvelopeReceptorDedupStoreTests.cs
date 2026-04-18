using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
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
/// Tests for the default, envelope-backed implementation of
/// <see cref="IReceptorDedupStore"/>.
/// </summary>
/// <remarks>
/// The envelope-backed store persists per-receptor invocation records directly on
/// <see cref="MessageEnvelope{TMessage}.ReceptorInvocations"/>, so they ride along with
/// the message to the next outbox / inbox write. No external state, no DB writes.
/// </remarks>
/// <docs>fundamentals/receptors/exactly-once-firing</docs>
public class EnvelopeReceptorDedupStoreTests {

  private sealed record TestMessage(string Value) : IMessage;

  private static MessageEnvelope<TestMessage> _newEnvelope() => new() {
    MessageId = MessageId.From(TrackedGuid.NewMedo()),
    Payload = new TestMessage("test"),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  [Test]
  public async Task TryGetPriorInvocation_ReturnsNullWhenEnvelopeHasNoInvocationsAsync() {
    var store = new EnvelopeReceptorDedupStore();
    var envelope = _newEnvelope();

    var prior = await store.TryGetPriorInvocationAsync(envelope, "SomeReceptor", CancellationToken.None);

    await Assert.That(prior).IsNull();
  }

  [Test]
  public async Task RecordInvocation_InitializesListWhenNullAsync() {
    var store = new EnvelopeReceptorDedupStore();
    var envelope = _newEnvelope();
    var record = new ReceptorInvocationRecord {
      ReceptorId = "SomeReceptor",
      Stage = LifecycleStage.PostInboxInline,
      CompletedAt = DateTimeOffset.UtcNow,
      Duration = TimeSpan.FromMilliseconds(10),
      ServiceName = "test-service"
    };

    await store.RecordInvocationAsync(envelope, record, CancellationToken.None);

    await Assert.That(envelope.ReceptorInvocations).IsNotNull();
    await Assert.That(envelope.ReceptorInvocations!).Count().IsEqualTo(1);
    await Assert.That(envelope.ReceptorInvocations![0]).IsEqualTo(record);
  }

  [Test]
  public async Task RecordInvocation_AppendsToExistingListAsync() {
    var store = new EnvelopeReceptorDedupStore();
    var envelope = _newEnvelope();
    envelope.ReceptorInvocations = [
      new ReceptorInvocationRecord {
        ReceptorId = "FirstReceptor",
        Stage = LifecycleStage.LocalImmediateInline,
        CompletedAt = DateTimeOffset.UtcNow.AddMilliseconds(-5),
        Duration = TimeSpan.FromMilliseconds(1),
        ServiceName = "test-service"
      }
    ];
    var record = new ReceptorInvocationRecord {
      ReceptorId = "SecondReceptor",
      Stage = LifecycleStage.PostInboxInline,
      CompletedAt = DateTimeOffset.UtcNow,
      Duration = TimeSpan.FromMilliseconds(2),
      ServiceName = "test-service"
    };

    await store.RecordInvocationAsync(envelope, record, CancellationToken.None);

    await Assert.That(envelope.ReceptorInvocations!).Count().IsEqualTo(2);
    await Assert.That(envelope.ReceptorInvocations![1]).IsEqualTo(record);
  }

  [Test]
  public async Task TryGetPriorInvocation_ReturnsRecordWhenReceptorPreviouslyRecordedAsync() {
    var store = new EnvelopeReceptorDedupStore();
    var envelope = _newEnvelope();
    var record = new ReceptorInvocationRecord {
      ReceptorId = "SomeReceptor",
      Stage = LifecycleStage.LocalImmediateInline,
      CompletedAt = DateTimeOffset.UtcNow,
      Duration = TimeSpan.FromMilliseconds(7),
      ServiceName = "test-service"
    };
    await store.RecordInvocationAsync(envelope, record, CancellationToken.None);

    var prior = await store.TryGetPriorInvocationAsync(envelope, "SomeReceptor", CancellationToken.None);

    await Assert.That(prior).IsNotNull();
    await Assert.That(prior!.ReceptorId).IsEqualTo("SomeReceptor");
    await Assert.That(prior.Stage).IsEqualTo(LifecycleStage.LocalImmediateInline);
  }

  [Test]
  public async Task TryGetPriorInvocation_DoesNotMatchDifferentReceptorIdAsync() {
    var store = new EnvelopeReceptorDedupStore();
    var envelope = _newEnvelope();
    await store.RecordInvocationAsync(envelope, new ReceptorInvocationRecord {
      ReceptorId = "ReceptorA",
      Stage = LifecycleStage.PostInboxInline,
      CompletedAt = DateTimeOffset.UtcNow,
      Duration = TimeSpan.Zero,
      ServiceName = "test-service"
    }, CancellationToken.None);

    var prior = await store.TryGetPriorInvocationAsync(envelope, "ReceptorB", CancellationToken.None);

    await Assert.That(prior).IsNull();
  }

  [Test]
  public async Task TryGetPriorInvocation_FindsPriorInvocationRegardlessOfStageAsync() {
    // The guardrail is PER-RECEPTOR, not per-stage. If a receptor fired at LocalImmediateInline
    // and then tries to fire at PreOutboxInline for the same message (e.g., filter bug), the
    // store must still return the prior invocation so the invoker can skip.
    var store = new EnvelopeReceptorDedupStore();
    var envelope = _newEnvelope();
    await store.RecordInvocationAsync(envelope, new ReceptorInvocationRecord {
      ReceptorId = "MyReceptor",
      Stage = LifecycleStage.LocalImmediateInline,
      CompletedAt = DateTimeOffset.UtcNow,
      Duration = TimeSpan.Zero,
      ServiceName = "test-service"
    }, CancellationToken.None);

    var prior = await store.TryGetPriorInvocationAsync(envelope, "MyReceptor", CancellationToken.None);

    await Assert.That(prior).IsNotNull();
    await Assert.That(prior!.Stage).IsEqualTo(LifecycleStage.LocalImmediateInline);
  }

  [Test]
  public async Task RoundtripsFaithfullyAsync() {
    var store = new EnvelopeReceptorDedupStore();
    var envelope = _newEnvelope();
    var now = DateTimeOffset.UtcNow;
    var duration = TimeSpan.FromMilliseconds(42);
    var input = new ReceptorInvocationRecord {
      ReceptorId = "MyReceptor",
      Stage = LifecycleStage.PostPerspectiveInline,
      CompletedAt = now,
      Duration = duration,
      ServiceName = "svc"
    };

    await store.RecordInvocationAsync(envelope, input, CancellationToken.None);
    var prior = await store.TryGetPriorInvocationAsync(envelope, "MyReceptor", CancellationToken.None);

    await Assert.That(prior).IsNotNull();
    await Assert.That(prior!.ReceptorId).IsEqualTo(input.ReceptorId);
    await Assert.That(prior.Stage).IsEqualTo(input.Stage);
    await Assert.That(prior.CompletedAt).IsEqualTo(input.CompletedAt);
    await Assert.That(prior.Duration).IsEqualTo(input.Duration);
    await Assert.That(prior.ServiceName).IsEqualTo(input.ServiceName);
  }
}
