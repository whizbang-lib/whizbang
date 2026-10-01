using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Sagas.Services;
using Whizbang.Sagas.Tests.Generators;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Everything a saga emits is stored on one stream, the saga's own: <see cref="SagaContext.SagaId"/>.
/// </summary>
/// <remarks>
/// <para>
/// The saga id is the framework's identity for a saga: the projection loader reads by it, per-item
/// streams derive from it, and the stranded-saga sweep, the watchdog tick, the abandonment and the
/// continuation request all key on it. The entity id is the consumer's domain identity, carried on
/// every event for filtering and routing, and frequently the same value, but not necessarily.
/// </para>
/// <para>
/// Generated saga events used to mark <c>EntityId</c> as their stream, and nothing resolved even that:
/// the consumer's stream id generator cannot see the classes the saga generator emits. Where the two
/// ids differ, a saga's lifecycle events and its framework events landed on different streams, and a
/// perspective could not fold them into one row.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Sagas.Generators/SagaGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Sagas/SagaFrameworkEventStreamIds.cs</code-under-test>
[Category("Saga")]
public class SagaStreamKeyTests {
  private static readonly Guid _sagaId = Guid.Parse("01900000-0000-7000-8000-00000000a100");
  private static readonly Guid _entityId = Guid.Parse("01900000-0000-7000-8000-00000000e100");

  [Test]
  public async Task EveryEventAGeneratedSagaEmits_IsStoredOnTheSagasStream_WhenTheEntityIdDiffersAsync() {
    await using var host = CapturedOutboxHost.Create();
    var emitter = new DispatcherSagaEventEmitter(host.Dispatcher, host.Claims);
    var saga = new GeneratorTestDefaultSaga.Service(emitter, NullLogger<GeneratorTestDefaultSaga.Service>.Instance);
    var ctx = new SagaContext(_sagaId, _entityId);

    await saga.InitiateSagaAsync(ctx, ["a", "b"], hookNames: ["notify"], CancellationToken.None);
    await saga.ItemsDispatchedAsync(ctx, 2, 2, 0, CancellationToken.None);
    await saga.UpdateItemAsync(ctx, "a", SagaItemState.Running, "Item a", CancellationToken.None);
    await saga.ResetItemAsync(ctx, "a", SagaItemState.Running, CancellationToken.None);
    await saga.UpdateItemAsync(ctx, "a", SagaItemState.Completed, "Item a", CancellationToken.None);
    await saga.FailItemAsync(ctx, "b", "boom", null, "Item b", CancellationToken.None);
    await saga.TryRunHookAsync(ctx, sagaProjection: null, "notify", null, _ => Task.CompletedTask, CancellationToken.None);
    await saga.TryRecoverViaWatchdogTickAsync(new SagaCompletionWatchdogTickEvent {
      StreamId = _sagaId,
      SagaName = GeneratorTestDefaultSaga.SagaName,
      EntityId = _entityId,
      LastObservedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
      ConsecutiveStallCount = new SagaOptions().MaxConsecutiveStalls - 1,
    }, CancellationToken.None);

    var rows = host.Outbox.Rows;
    var emitted = rows.Select(r => r.Payload.GetType().Name).ToHashSet();
    await Assert.That(emitted).IsEquivalentTo([
      nameof(GeneratorTestDefaultSaga.InitiatedEvent), nameof(SagaCompletionWatchdogTickEvent),
      nameof(GeneratorTestDefaultSaga.ItemsDispatchedEvent), nameof(GeneratorTestDefaultSaga.ItemStartedEvent),
      nameof(GeneratorTestDefaultSaga.ResetEvent), nameof(GeneratorTestDefaultSaga.ItemCompletedEvent),
      nameof(GeneratorTestDefaultSaga.ItemFailedEvent), nameof(GeneratorTestDefaultSaga.CompletedEvent),
      nameof(GeneratorTestDefaultSaga.HookStartedEvent), nameof(GeneratorTestDefaultSaga.HookCompletedEvent),
      nameof(SagaCompletionAbandonedEvent),
    ]).Because("the precondition: every kind of event the saga emits was published");
    foreach (var row in rows) {
      await Assert.That(row.Message.StreamId).IsEqualTo(_sagaId)
        .Because($"{row.Payload.GetType().Name} belongs to the saga; on another stream no perspective can fold it into the saga's row");
    }
  }

  /// <summary>The events still carry the entity id, for the consumer's filtering and routing.</summary>
  [Test]
  public async Task GeneratedSagaEvents_CarryBothIdsAsync() {
    await using var host = CapturedOutboxHost.Create();
    var saga = new GeneratorTestDefaultSaga.Service(new DispatcherSagaEventEmitter(host.Dispatcher), NullLogger<GeneratorTestDefaultSaga.Service>.Instance);

    await saga.InitiateSagaAsync(new SagaContext(_sagaId, _entityId), ["a"], hookNames: null, CancellationToken.None);

    var initiated = host.Outbox.Rows.Select(r => r.Payload).OfType<GeneratorTestDefaultSaga.InitiatedEvent>().Single();
    await Assert.That(initiated.SagaId).IsEqualTo(_sagaId);
    await Assert.That(initiated.EntityId).IsEqualTo(_entityId);
  }

  /// <summary>
  /// A saga declared over its own event base is still stored on the saga's stream, even when the base
  /// marks a stream id of its own.
  /// </summary>
  /// <remarks>
  /// The base's <c>[StreamId]</c> is resolved by the extractor generated for the consumer's assembly,
  /// which matches derived types too. The saga extractor is asked first, so a saga event belongs to its
  /// saga whatever its base says.
  /// </remarks>
  [Test]
  public async Task CustomBaseSagaEvent_IsStoredOnTheSagasStream_NotTheBasesAsync() {
    await using var host = CapturedOutboxHost.Create();
    var initiated = new GeneratorTestCustomBaseSaga.InitiatedEvent { SagaId = _sagaId, EntityId = _entityId, StreamEntityId = Guid.CreateVersion7() };

    await host.Dispatcher.PublishAsync(initiated);

    await Assert.That(host.Outbox.Rows.Single().Message.StreamId).IsEqualTo(_sagaId);
  }
}
