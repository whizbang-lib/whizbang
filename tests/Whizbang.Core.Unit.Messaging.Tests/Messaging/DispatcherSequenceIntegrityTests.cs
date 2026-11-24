// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The dispatcher decides each outgoing event's sequence integrity at the origin and writes it onto the outbox row it
/// builds: the dispatch's own setting first, then the type's, then the configured default. A command, and an event that
/// is only stored, is never held.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
[Category("Unit")]
public class DispatcherSequenceIntegrityTests {
  public record PlaceOrder(Guid OrderId) : ICommand;
  public record OrderPlaced([property: StreamId] Guid OrderId) : IEvent;
  public record LedgerPosted([property: StreamId] Guid LedgerId) : IEvent;

  private sealed class Catalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(OrderPlaced), TypeNameFormatter.FormatClrTypeName(typeof(OrderPlaced)), "event", null),
      new(typeof(LedgerPosted), TypeNameFormatter.FormatClrTypeName(typeof(LedgerPosted)), "event", null) {
        SequenceIntegrity = SequenceIntegrity.UntilStamped
      },
    ];
  }

  private sealed class RecordingStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> QueuedOutbox { get; } = [];
    public void QueueOutboxMessage(OutboxMessage message) => QueuedOutbox.Add(message);
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
  }

  private sealed class TestScopeFactory(IServiceProvider provider) : IServiceScopeFactory {
    public IServiceScope CreateScope() => new TestScope(provider);
    private sealed class TestScope(IServiceProvider provider) : IServiceScope {
      public IServiceProvider ServiceProvider { get; } = provider;
      public void Dispose() { }
    }
  }

  /// <summary>No local receptors, so every message routes to the outbox; exposes the cascade path.</summary>
  private sealed class OutboxOnlyDispatcher(IServiceProvider sp) : Core.Dispatcher(
      sp, new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build())) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) => _ => Task.CompletedTask;
    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) => null;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => null;

    public Task CascadeAsync(IMessage message, bool eventStoreOnly = false) =>
      PublishToOutboxDynamicAsync(message, message.GetType(), Whizbang.Core.ValueObjects.MessageId.New(), eventStoreOnly: eventStoreOnly);
  }

  private static (OutboxOnlyDispatcher Dispatcher, RecordingStrategy Strategy) _dispatcher(WhizbangCoreOptions? options = null) {
    var strategy = new RecordingStrategy();
    var services = new ServiceCollection();
    services.AddSingleton(options ?? new WhizbangCoreOptions());
    services.AddSingleton<IMessageTypeCatalog>(new Catalog());
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceScopeFactory>(sp => new TestScopeFactory(sp));
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    return (new OutboxOnlyDispatcher(services.BuildServiceProvider()), strategy);
  }

  [Test]
  public async Task Publish_AnEventWithNothingSet_IsHeldBoundedForTheDefaultSecondAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.PublishAsync(new OrderPlaced(Guid.CreateVersion7()));

    await Assert.That(strategy.QueuedOutbox).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutbox[0].SequenceHoldMilliseconds).IsEqualTo(1000);
  }

  [Test]
  public async Task Publish_TheConfiguredDefault_TheTypesAttribute_AndTheDispatch_InOrderOfPrecedenceAsync() {
    var (dispatcher, strategy) = _dispatcher(new WhizbangCoreOptions { SequenceIntegrity = SequenceIntegrity.None });

    await dispatcher.PublishAsync(new OrderPlaced(Guid.CreateVersion7()));
    await dispatcher.PublishAsync(new LedgerPosted(Guid.CreateVersion7()));
    await dispatcher.PublishAsync(new LedgerPosted(Guid.CreateVersion7()),
      new DispatchOptions().WithSequenceIntegrity(SequenceIntegrity.Bounded, TimeSpan.FromMilliseconds(400)));

    await Assert.That(strategy.QueuedOutbox.Select(m => m.SequenceHoldMilliseconds))
      .IsEquivalentTo([0, SequenceHold.UNTIL_STAMPED_MILLISECONDS, 400]);
  }

  [Test]
  public async Task PublishMany_CarriesTheDispatchsSettingToEveryEventAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.PublishManyAsync(
      [new OrderPlaced(Guid.CreateVersion7()), new OrderPlaced(Guid.CreateVersion7())],
      new DispatchOptions().WithSequenceIntegrity(SequenceIntegrity.UntilStamped));

    await Assert.That(strategy.QueuedOutbox).Count().IsEqualTo(2);
    await Assert.That(strategy.QueuedOutbox.All(m => m.SequenceHoldMilliseconds == SequenceHold.UNTIL_STAMPED_MILLISECONDS)).IsTrue();
  }

  [Test]
  public async Task Send_ACommand_IsNeverHeldWhateverTheSettingAsync() {
    var (dispatcher, strategy) = _dispatcher(new WhizbangCoreOptions { SequenceIntegrity = SequenceIntegrity.UntilStamped });

    await dispatcher.SendAsync(new PlaceOrder(Guid.CreateVersion7()),
      new DispatchOptions().WithSequenceIntegrity(SequenceIntegrity.UntilStamped));

    await Assert.That(strategy.QueuedOutbox).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutbox[0].SequenceHoldMilliseconds).IsEqualTo(0)
      .Because("only an event is stamped with a commit sequence, so only an event can wait for one");
  }

  [Test]
  public async Task Cascade_AnEvent_TakesItsTypesSetting_AndAnEventOnlyStoredIsNeverHeldAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.CascadeAsync(new LedgerPosted(Guid.CreateVersion7()));
    await dispatcher.CascadeAsync(new OrderPlaced(Guid.CreateVersion7()));
    await dispatcher.CascadeAsync(new LedgerPosted(Guid.CreateVersion7()), eventStoreOnly: true);

    await Assert.That(strategy.QueuedOutbox.Select(m => m.SequenceHoldMilliseconds))
      .IsEquivalentTo([SequenceHold.UNTIL_STAMPED_MILLISECONDS, 1000, 0]);
  }
}
