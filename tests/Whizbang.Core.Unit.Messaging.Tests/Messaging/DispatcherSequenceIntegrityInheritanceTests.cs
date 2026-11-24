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
/// Sequence integrity follows priority: an ambient scope applies to every dispatch inside it that names no setting
/// itself, and what a receptor produces (an event it returns, or anything it dispatches while handling) inherits the
/// parent dispatch's setting when the parent's was explicit. Precedence, most specific first: the dispatch's own
/// options, the ambient scope or inherited parent setting, the type's attribute, the configuration, the default.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/SequenceIntegrityContext.cs</code-under-test>
[Category("Unit")]
public class DispatcherSequenceIntegrityInheritanceTests {
  public record CreateItem(Guid ItemId) : ICommand;
  public record CreateItemAndPublish(Guid ItemId) : ICommand;
  public record CreateItemAndPublishAsStamped(Guid ItemId) : ICommand;
  public record PostLedger(Guid LedgerId) : ICommand;
  public record NotifyIndexer(Guid ItemId) : ICommand;
  public record ItemInitialized([property: StreamId] Guid ItemId) : IEvent;
  public record ItemIndexed([property: StreamId] Guid ItemId) : IEvent;
  public record LedgerPosted([property: StreamId] Guid LedgerId) : IEvent;

  private const int DEFAULT_HOLD = 1000;

  private sealed class Catalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(ItemInitialized), TypeNameFormatter.FormatClrTypeName(typeof(ItemInitialized)), "event", null),
      new(typeof(ItemIndexed), TypeNameFormatter.FormatClrTypeName(typeof(ItemIndexed)), "event", null),
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

    /// <summary>The stored hold of the one row carrying a message of <typeparamref name="T"/>.</summary>
    public int HoldOf<T>() =>
      QueuedOutbox.Single(m => m.MessageType.Contains(typeof(T).Name, StringComparison.Ordinal)).SequenceHoldMilliseconds;
  }

  private sealed class TestScopeFactory(IServiceProvider provider) : IServiceScopeFactory {
    public IServiceScope CreateScope() => new TestScope(provider);
    private sealed class TestScope(IServiceProvider provider) : IServiceScope {
      public IServiceProvider ServiceProvider { get; } = provider;
      public void Dispose() { }
    }
  }

  /// <summary>
  /// Local receptors for the commands: <see cref="CreateItem"/> returns <see cref="ItemInitialized"/>, whose own receptor
  /// cascades <see cref="ItemIndexed"/> (a chain of two), whether the event is cascaded or published; the publishing commands dispatch the event from inside the
  /// handling instead of returning it; <see cref="PostLedger"/> returns an event whose type holds until stamped.
  /// <see cref="NotifyIndexer"/> has no receptor, so it goes to the outbox as a command.
  /// </summary>
  private sealed class ReceptorDispatcher(IServiceProvider sp) : Core.Dispatcher(
      sp, new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build())) {
    protected override ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) => message switch {
      CreateItem create => _ => ValueTask.FromResult((TResult)(object)new ItemInitialized(create.ItemId)),
      PostLedger post => _ => ValueTask.FromResult((TResult)(object)new LedgerPosted(post.LedgerId)),
      CreateItemAndPublish create => async _ => await _publishAsync<TResult>(new ItemInitialized(create.ItemId), null),
      CreateItemAndPublishAsStamped create => async _ => await _publishAsync<TResult>(new ItemInitialized(create.ItemId),
        new DispatchOptions().WithSequenceIntegrity(SequenceIntegrity.UntilStamped)),
      _ => null,
    };

    private async Task<TResult> _publishAsync<TResult>(ItemInitialized evt, DispatchOptions? options) {
      await (options is null ? PublishAsync(evt) : PublishAsync(evt, options));
      return default!;
    }

    protected override Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType) =>
      eventType == typeof(ItemInitialized)
        ? (message, envelope, ct) => CascadeMessageAsync(new ItemIndexed(((ItemInitialized)message).ItemId), envelope, DispatchModes.Outbox, ct)
        : null;

    protected override Task CascadeToOutboxAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) =>
      CascadeByRuntimeTypeAsync(message, messageType, sourceEnvelope, eventId, eventStoreOnly: false);

    protected override DispatchModes? GetReceptorDefaultRouting(Type messageType) => DispatchModes.LocalDispatch | DispatchModes.Outbox;
    protected override VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType) => null;
    protected override ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType) =>
      published => published is ItemInitialized initialized
        ? CascadeMessageAsync(new ItemIndexed(initialized.ItemId), null, DispatchModes.Outbox)
        : Task.CompletedTask;
    protected override SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType) => null;
    protected override VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType) => null;
    protected override Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType) => null;
  }

  private static (ReceptorDispatcher Dispatcher, RecordingStrategy Strategy) _dispatcher() {
    var strategy = new RecordingStrategy();
    var services = new ServiceCollection();
    services.AddSingleton(new WhizbangCoreOptions());
    services.AddSingleton<IMessageTypeCatalog>(new Catalog());
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceScopeFactory>(sp => new TestScopeFactory(sp));
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    return (new ReceptorDispatcher(services.BuildServiceProvider()), strategy);
  }

  private static DispatchOptions _live() => new DispatchOptions().WithSequenceIntegrity(SequenceIntegrity.None);

  [Test]
  public async Task Publish_InsideTheScope_TakesTheScopesSettingAsync() {
    var (dispatcher, strategy) = _dispatcher();

    using (SequenceIntegrityContext.Enter(SequenceIntegrity.Bounded, TimeSpan.FromMilliseconds(250))) {
      await dispatcher.PublishAsync(new LedgerPosted(Guid.CreateVersion7()));
    }

    await Assert.That(strategy.HoldOf<LedgerPosted>()).IsEqualTo(250)
      .Because("the scope is more specific than the type's attribute, which would hold until stamped");
  }

  [Test]
  public async Task Publish_AfterANestedScopeIsDisposed_TakesTheOuterOneAsync() {
    var (dispatcher, strategy) = _dispatcher();

    using (SequenceIntegrityContext.Enter(SequenceIntegrity.Bounded, TimeSpan.FromMilliseconds(250))) {
      using (SequenceIntegrityContext.Enter(SequenceIntegrity.None)) {
        await dispatcher.PublishAsync(new ItemIndexed(Guid.CreateVersion7()));
      }
      await dispatcher.PublishAsync(new LedgerPosted(Guid.CreateVersion7()));
    }

    await Assert.That(strategy.HoldOf<ItemIndexed>()).IsEqualTo(0);
    await Assert.That(strategy.HoldOf<LedgerPosted>()).IsEqualTo(250);
  }

  [Test]
  public async Task Publish_ItsOwnSetting_BeatsTheScopeAsync() {
    var (dispatcher, strategy) = _dispatcher();

    using (SequenceIntegrityContext.Enter(SequenceIntegrity.None)) {
      await dispatcher.PublishAsync(new ItemInitialized(Guid.CreateVersion7()),
        new DispatchOptions().WithSequenceIntegrity(SequenceIntegrity.UntilStamped));
    }

    await Assert.That(strategy.HoldOf<ItemInitialized>()).IsEqualTo(SequenceHold.UNTIL_STAMPED_MILLISECONDS);
  }

  [Test]
  public async Task Cascade_InheritsItsParentsExplicitSettingAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.SendAsync(new PostLedger(Guid.CreateVersion7()), _live());

    await Assert.That(strategy.HoldOf<LedgerPosted>()).IsEqualTo(0)
      .Because("the event the receptor returned inherits the command's explicit setting over its type's attribute");
  }

  [Test]
  public async Task Cascade_InheritsItsParentsHoldTimeAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.SendAsync(new CreateItem(Guid.CreateVersion7()),
      new DispatchOptions().WithSequenceIntegrity(SequenceIntegrity.Bounded, TimeSpan.FromMilliseconds(300)));

    await Assert.That(strategy.HoldOf<ItemInitialized>()).IsEqualTo(300);
  }

  [Test]
  public async Task Cascade_AChainOfTwo_InheritsTheRootsExplicitSettingAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.SendAsync(new CreateItem(Guid.CreateVersion7()), _live());

    await Assert.That(strategy.HoldOf<ItemInitialized>()).IsEqualTo(0);
    await Assert.That(strategy.HoldOf<ItemIndexed>()).IsEqualTo(0)
      .Because("the event cascaded by the cascaded event's own receptor inherits the same setting");
  }

  [Test]
  public async Task Cascade_FromALocalInvoke_InheritsItsExplicitSettingAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.LocalInvokeAsync<object>(new PostLedger(Guid.CreateVersion7()), _live());

    await Assert.That(strategy.HoldOf<LedgerPosted>()).IsEqualTo(0);
  }

  [Test]
  public async Task Cascade_FromALocalInvokeWithReceipt_InheritsItsExplicitSettingAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.LocalInvokeWithReceiptAsync<object>(new PostLedger(Guid.CreateVersion7()), _live());

    await Assert.That(strategy.HoldOf<LedgerPosted>()).IsEqualTo(0);
  }

  [Test]
  public async Task Cascade_FromABatch_InheritsTheBatchsExplicitSettingAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.SendManyAsync<object>([new PostLedger(Guid.CreateVersion7()), new CreateItem(Guid.CreateVersion7())], _live());

    await Assert.That(strategy.HoldOf<LedgerPosted>()).IsEqualTo(0);
    await Assert.That(strategy.HoldOf<ItemInitialized>()).IsEqualTo(0);
    await Assert.That(strategy.HoldOf<ItemIndexed>()).IsEqualTo(0);
  }

  [Test]
  public async Task Cascade_InheritsTheAmbientScopeAsync() {
    var (dispatcher, strategy) = _dispatcher();

    using (SequenceIntegrityContext.Enter(SequenceIntegrity.None)) {
      await dispatcher.SendAsync(new PostLedger(Guid.CreateVersion7()));
    }

    await Assert.That(strategy.HoldOf<LedgerPosted>()).IsEqualTo(0);
  }

  [Test]
  public async Task ADispatchMadeWhileHandling_InheritsTheParentsExplicitSettingAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.SendAsync(new CreateItemAndPublish(Guid.CreateVersion7()), _live());

    await Assert.That(strategy.HoldOf<ItemInitialized>()).IsEqualTo(0);
    await Assert.That(strategy.HoldOf<ItemIndexed>()).IsEqualTo(0);
  }

  [Test]
  public async Task ADispatchMadeWhileHandling_ItsOwnSettingWinsOverTheParentsAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.SendAsync(new CreateItemAndPublishAsStamped(Guid.CreateVersion7()), _live());

    await Assert.That(strategy.HoldOf<ItemInitialized>()).IsEqualTo(SequenceHold.UNTIL_STAMPED_MILLISECONDS)
      .Because("the cascade's own dispatch options are more specific than anything it inherits");
    await Assert.That(strategy.HoldOf<ItemIndexed>()).IsEqualTo(SequenceHold.UNTIL_STAMPED_MILLISECONDS)
      .Because("and that explicit setting is in turn what its own cascades inherit");
  }

  [Test]
  public async Task Cascade_WithNothingExplicit_TakesTheTypesAttributeAndTheDefaultAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.SendAsync(new PostLedger(Guid.CreateVersion7()));
    await dispatcher.SendAsync(new CreateItem(Guid.CreateVersion7()));

    await Assert.That(strategy.HoldOf<LedgerPosted>()).IsEqualTo(SequenceHold.UNTIL_STAMPED_MILLISECONDS);
    await Assert.That(strategy.HoldOf<ItemInitialized>()).IsEqualTo(DEFAULT_HOLD);
    await Assert.That(strategy.HoldOf<ItemIndexed>()).IsEqualTo(DEFAULT_HOLD);
  }

  [Test]
  public async Task Cascade_OnceTheParentsDispatchReturns_NoLongerInheritsAsync() {
    var (dispatcher, strategy) = _dispatcher();

    await dispatcher.SendAsync(new CreateItem(Guid.CreateVersion7()), _live());
    await dispatcher.SendAsync(new PostLedger(Guid.CreateVersion7()));

    await Assert.That(strategy.HoldOf<LedgerPosted>()).IsEqualTo(SequenceHold.UNTIL_STAMPED_MILLISECONDS)
      .Because("a parent's setting covers its own handling only, never the dispatches that follow it");
  }

  [Test]
  public async Task ACommand_IsNeverHeld_InsideAScopeOrUnderAnExplicitSettingAsync() {
    var (dispatcher, strategy) = _dispatcher();

    using (SequenceIntegrityContext.Enter(SequenceIntegrity.UntilStamped)) {
      await dispatcher.SendAsync(new NotifyIndexer(Guid.CreateVersion7()),
        new DispatchOptions().WithSequenceIntegrity(SequenceIntegrity.UntilStamped));
    }

    await Assert.That(strategy.HoldOf<NotifyIndexer>()).IsEqualTo(0)
      .Because("only an event is stamped with a commit sequence, so only an event can wait for one");
  }
}
