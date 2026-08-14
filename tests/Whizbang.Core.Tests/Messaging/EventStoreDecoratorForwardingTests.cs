using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Events;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.SystemEvents;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Locks that every event-store decorator FORWARDS the interface's default-implemented methods
/// to the inner store. A default interface method is NOT virtual dispatch through composition:
/// a decorator that doesn't explicitly forward silently serves the interface default instead of
/// the inner store's override — which is how a decorated <c>EFCoreEventStore</c> lost
/// <c>GetCommitSequenceAsync</c> (snapshot commit-sequence anchors silently null) and
/// <c>HasStreamEventsBeforeAsync</c> (resurrection-on-wake never fired through an auditing
/// decoration; caught by a consumer wake-after-reap E2E during rollout).
/// </summary>
/// <docs>fundamentals/perspectives/row-retention</docs>
public class EventStoreDecoratorForwardingTests {
  public static IEnumerable<Func<IEventStore>> Decorators() => [
    () => new SecurityContextEventStoreDecorator(new ProbeAwareStore()),
    () => new AppendAndWaitEventStoreDecorator(new ProbeAwareStore(), new NoopSyncAwaiter()),
    () => new AuditingEventStoreDecorator(new ProbeAwareStore(), new NoopOutboxChannel(), Options.Create(new SystemEventOptions())),
    () => new SyncTrackingEventStoreDecorator(new ProbeAwareStore()),
    () => new UpcastingEventStoreDecorator(new ProbeAwareStore(), new EventUpcasterPipeline([])),
  ];

  /// <summary>
  /// Interface members whose default implementation is DELIBERATELY served by decorators:
  /// <c>AppendBatchAsync</c>'s default loops over <c>this.AppendAsync</c>, so interceptors
  /// (audit, sync-tracking, security-context) see every entry — forwarding it to the inner
  /// store would bypass them. <c>AppendAndWaitAsync</c> is only meaningful on the outermost
  /// decorator; inner layers keep the interface default. Every OTHER default member must be
  /// explicitly forwarded, or the decorator swallows the inner store's override.
  /// </summary>
  private static readonly HashSet<string> _deliberateInterfaceDefaults = [
    nameof(IEventStore.AppendBatchAsync),
    nameof(IEventStore.AppendAndWaitAsync),
  ];

  public static IEnumerable<Func<Type>> DecoratorTypes() => [
    () => typeof(SecurityContextEventStoreDecorator),
    () => typeof(AppendAndWaitEventStoreDecorator),
    () => typeof(AuditingEventStoreDecorator),
    () => typeof(SyncTrackingEventStoreDecorator),
    () => typeof(UpcastingEventStoreDecorator),
  ];

  /// <summary>
  /// Drift-lock for FUTURE default interface methods: any <see cref="IEventStore"/> member a
  /// decorator leaves to the interface default (instead of a class-declared forward) must be on
  /// the deliberate allow-list. When a new default member is added to the interface, this fails
  /// for every decorator until the author either forwards it or consciously allow-lists it.
  /// </summary>
  [Test]
  [MethodDataSource(nameof(DecoratorTypes))]
  public async Task Decorator_LeavesNoDefaultInterfaceMethod_UnforwardedAsync(Type decoratorType) {
    var map = decoratorType.GetInterfaceMap(typeof(IEventStore));
    var swallowed = map.TargetMethods
      .Where(m => m.DeclaringType?.IsInterface == true)
      .Select(m => m.Name.Split('.')[^1])
      .Where(name => !_deliberateInterfaceDefaults.Contains(name))
      .Distinct()
      .ToList();

    await Assert.That(swallowed).IsEmpty()
      .Because($"{decoratorType.Name} serves the interface default for [{string.Join(", ", swallowed)}] — forward to the inner store or add to the deliberate allow-list");
  }

  [Test]
  [MethodDataSource(nameof(Decorators))]
  public async Task Decorator_ForwardsGetCommitSequence_ToInnerStoreAsync(IEventStore decorated) {
    await Assert.That(await decorated.GetCommitSequenceAsync(Guid.NewGuid())).IsEqualTo(42L)
      .Because($"{decorated.GetType().Name} must forward to the inner store's override, not serve the interface default (null)");
  }

  [Test]
  [MethodDataSource(nameof(Decorators))]
  public async Task Decorator_ForwardsHasStreamEventsBefore_ToInnerStoreAsync(IEventStore decorated) {
    await Assert.That(await decorated.HasStreamEventsBeforeAsync(Guid.NewGuid(), Guid.NewGuid())).IsTrue()
      .Because($"{decorated.GetType().Name} must forward to the inner store's override, not serve the interface default (false) — a swallowed probe disables resurrection-on-wake");
  }

  /// <summary>
  /// An inner store whose probe methods return sentinel values distinguishable from the
  /// interface defaults (null / false) — forwarding is proven iff the sentinel surfaces
  /// through the decorator.
  /// </summary>
  private sealed class ProbeAwareStore : IEventStore {
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default)
        where TMessage : notnull =>
      Task.CompletedTask;

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<IEvent>>();

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<TMessage>>());

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<IEvent>>());

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(0L);

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];

    public Task<long?> GetCommitSequenceAsync(Guid eventId, CancellationToken cancellationToken = default) =>
      Task.FromResult<long?>(42L);

    public Task<bool> HasStreamEventsBeforeAsync(Guid streamId, Guid beforeEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);
  }

  private sealed class NoopSyncAwaiter : IPerspectiveSyncAwaiter {
    public Guid AwaiterId { get; } = Guid.NewGuid();

    public Task<SyncResult> WaitAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) =>
      throw new NotSupportedException("Not used by the forwarding tests");

    public Task<bool> IsCaughtUpAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) =>
      throw new NotSupportedException("Not used by the forwarding tests");

    public Task<SyncResult> WaitForStreamAsync(
        Type perspectiveType,
        Guid streamId,
        Type[]? eventTypes,
        TimeSpan timeout,
        Guid? eventIdToAwait = null,
        CancellationToken ct = default) =>
      throw new NotSupportedException("Not used by the forwarding tests");
  }

  private sealed class NoopOutboxChannel : IDeferredOutboxChannel {
    public ValueTask QueueAsync(OutboxMessage message, CancellationToken ct = default) => ValueTask.CompletedTask;

    public IReadOnlyList<OutboxMessage> DrainAll() => [];

    public bool HasPending => false;
  }
}
