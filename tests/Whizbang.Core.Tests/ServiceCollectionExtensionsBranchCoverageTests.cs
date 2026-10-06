// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests;

/// <summary>
/// Branch backfill for <c>DecorateEventStoreWithSyncTracking</c>'s
/// upcasting layer, for both the scoped and the singleton registration shapes: a pipeline with
/// upcasters wraps the inner store so reads come back upcasted, and an empty pipeline leaves reads
/// exactly as the inner store produced them.
/// </summary>
public class ServiceCollectionExtensionsBranchCoverageTests {

  private sealed record LegacyEvent(string Value) : IEvent;
  private sealed record CurrentEvent(string Value) : IEvent;

  private sealed class LegacyToCurrentUpcaster : IEventUpcaster {
    public bool CanUpcast(IEvent storedEvent) => storedEvent is LegacyEvent;
    public IEvent Upcast(IEvent storedEvent) => new CurrentEvent(((LegacyEvent)storedEvent).Value);
  }

  [Test]
  public async Task Decorate_ScopedStore_WithUpcasters_ReadsComeBackUpcastedAsync() {
    var read = await _readThroughDecoratedStoreAsync(scoped: true, new EventUpcasterPipeline([new LegacyToCurrentUpcaster()]));

    await Assert.That(read).IsTypeOf<CurrentEvent>()
      .Because("with upcasters registered the inner store is wrapped, so every read sees the current shape");
  }

  [Test]
  public async Task Decorate_ScopedStore_WithAnEmptyPipeline_ReadsPassThroughUnchangedAsync() {
    var read = await _readThroughDecoratedStoreAsync(scoped: true, new EventUpcasterPipeline([]));

    await Assert.That(read).IsTypeOf<LegacyEvent>()
      .Because("an empty pipeline does not wrap the store, so reads are exactly what the inner store produced");
  }

  [Test]
  public async Task Decorate_SingletonStore_WithUpcasters_ReadsComeBackUpcastedAsync() {
    var read = await _readThroughDecoratedStoreAsync(scoped: false, new EventUpcasterPipeline([new LegacyToCurrentUpcaster()]));

    await Assert.That(read).IsTypeOf<CurrentEvent>()
      .Because("the singleton registration shape wraps with the upcasting layer too");
  }

  [Test]
  public async Task Decorate_SingletonStore_WithAnEmptyPipeline_ReadsPassThroughUnchangedAsync() {
    var read = await _readThroughDecoratedStoreAsync(scoped: false, new EventUpcasterPipeline([]));

    await Assert.That(read).IsTypeOf<LegacyEvent>();
  }

  private static async Task<IEvent> _readThroughDecoratedStoreAsync(bool scoped, EventUpcasterPipeline pipeline) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    _ = services.AddWhizbang();
    services.AddScoped<IWorkCoordinator, StubWorkCoordinator>();
    services.AddLogging();
    services.AddSingleton(pipeline);
    if (scoped) {
      services.AddScoped<IEventStore>(_ => new LegacyReturningEventStore());
    } else {
      services.AddSingleton<IEventStore>(_ => new LegacyReturningEventStore());
    }

    services.DecorateEventStoreWithSyncTracking();

    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
    var events = store.DeserializeStreamEvents([], []);
    return events.Single().Payload;
  }

  /// <summary>An inner store whose stream deserialization yields one legacy-shaped event.</summary>
  private sealed class LegacyReturningEventStore : IEventStore {
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default)
      where TMessage : notnull =>
      Task.CompletedTask;

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      System.Linq.AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      System.Linq.AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
      Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      System.Linq.AsyncEnumerable.Empty<MessageEnvelope<IEvent>>();

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(
      Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<TMessage>>());

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<IEvent>>());

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(0L);

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      [new MessageEnvelope<IEvent> { MessageId = MessageId.New(), Payload = new LegacyEvent("stored"), Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local } }];
  }

  private sealed class StubWorkCoordinator : IWorkCoordinator {
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
