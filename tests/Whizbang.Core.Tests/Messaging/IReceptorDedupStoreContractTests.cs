using System;
using System.Threading;
using System.Threading.Tasks;
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
/// Abstract contract test base for <see cref="IReceptorDedupStore"/> implementations.
/// Subclasses provide a concrete store via <see cref="CreateStore"/>; they automatically
/// inherit the full contract suite. Future implementations (e.g., a database-backed
/// <c>DatabaseReceptorDedupStore</c>) should derive from this class and only add
/// implementation-specific tests — the contract surface stays uniform.
/// </summary>
/// <remarks>
/// Uses TUnit's <see cref="InheritsTestsAttribute"/> so the subclass automatically
/// inherits every <c>[Test]</c> method declared here.
/// </remarks>
/// <docs>fundamentals/receptors/exactly-once-firing</docs>
public abstract class IReceptorDedupStoreContractTests {

  /// <summary>
  /// Factory for the implementation under test. Called once per test method so each test
  /// gets a fresh store (matters for stateful implementations).
  /// </summary>
  protected abstract IReceptorDedupStore CreateStore();

  private sealed record TestMessage(string Value) : IMessage;

  private static MessageEnvelope<TestMessage> _newEnvelope() => new() {
    MessageId = MessageId.From(TrackedGuid.NewMedo()),
    Payload = new TestMessage("test"),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };

  private static ReceptorInvocationRecord _newRecord(string receptorId, LifecycleStage stage) => new() {
    ReceptorId = receptorId,
    Stage = stage,
    CompletedAt = DateTimeOffset.UtcNow,
    Duration = TimeSpan.FromMilliseconds(5),
    ServiceName = "test-service"
  };

  [Test]
  public async Task Contract_TryGetPriorInvocation_ReturnsNullForUnseenReceptorAsync() {
    var store = CreateStore();
    var envelope = _newEnvelope();

    var prior = await store.TryGetPriorInvocationAsync(envelope, "Unseen", CancellationToken.None);

    await Assert.That(prior).IsNull();
  }

  [Test]
  public async Task Contract_RecordInvocationThenTryGet_ReturnsRecordedValueAsync() {
    var store = CreateStore();
    var envelope = _newEnvelope();
    var record = _newRecord("R1", LifecycleStage.PostInboxInline);

    await store.RecordInvocationAsync(envelope, record, CancellationToken.None);
    var prior = await store.TryGetPriorInvocationAsync(envelope, "R1", CancellationToken.None);

    await Assert.That(prior).IsNotNull();
    await Assert.That(prior!.ReceptorId).IsEqualTo("R1");
    await Assert.That(prior.Stage).IsEqualTo(LifecycleStage.PostInboxInline);
  }

  [Test]
  public async Task Contract_DifferentReceptorIdsDoNotInterfereAsync() {
    var store = CreateStore();
    var envelope = _newEnvelope();

    await store.RecordInvocationAsync(envelope, _newRecord("R1", LifecycleStage.PostInboxInline), CancellationToken.None);

    var priorR1 = await store.TryGetPriorInvocationAsync(envelope, "R1", CancellationToken.None);
    var priorR2 = await store.TryGetPriorInvocationAsync(envelope, "R2", CancellationToken.None);

    await Assert.That(priorR1).IsNotNull();
    await Assert.That(priorR2).IsNull();
  }

  [Test]
  public async Task Contract_SameReceptorAcrossEnvelopesIsIndependentAsync() {
    // Store must partition by envelope / message id — a record on envelope A must not leak
    // into a query against envelope B.
    var store = CreateStore();
    var envelopeA = _newEnvelope();
    var envelopeB = _newEnvelope();

    await store.RecordInvocationAsync(envelopeA, _newRecord("Rx", LifecycleStage.PostInboxInline), CancellationToken.None);

    var priorOnB = await store.TryGetPriorInvocationAsync(envelopeB, "Rx", CancellationToken.None);

    await Assert.That(priorOnB).IsNull();
  }

  [Test]
  public async Task Contract_PriorInvocationIsReturnedRegardlessOfStageAsync() {
    // The guardrail is per-receptor: a receptor that fired at LocalImmediateInline must
    // be blocked from re-firing at PreOutboxInline. TryGetPriorInvocationAsync returns
    // a match regardless of which stage the prior fired at.
    var store = CreateStore();
    var envelope = _newEnvelope();
    await store.RecordInvocationAsync(envelope, _newRecord("Rx", LifecycleStage.LocalImmediateInline), CancellationToken.None);

    var prior = await store.TryGetPriorInvocationAsync(envelope, "Rx", CancellationToken.None);

    await Assert.That(prior).IsNotNull();
    await Assert.That(prior!.Stage).IsEqualTo(LifecycleStage.LocalImmediateInline);
  }
}

/// <summary>
/// Concrete contract tests for the default envelope-backed implementation.
/// Demonstrates the inheritance pattern for future store implementations.
/// </summary>
[InheritsTests]
public sealed class EnvelopeReceptorDedupStoreContractTests : IReceptorDedupStoreContractTests {
  protected override IReceptorDedupStore CreateStore() => new EnvelopeReceptorDedupStore();
}
