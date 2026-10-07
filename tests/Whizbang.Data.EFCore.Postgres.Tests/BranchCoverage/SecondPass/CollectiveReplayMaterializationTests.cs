// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.SecondPass;

/// <summary>
/// The replay applier reads the tenant's collective positions from the event-store query whichever way
/// the provider materializes it: an asynchronous provider (Entity Framework) is enumerated as an
/// asynchronous sequence, any other (Dapper's in-memory query) as a plain one. Both ways must find the
/// same collective and fold it into the stream. No database.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Collective/CollectiveReplayApplier.cs</code-under-test>
[Category("Unit")]
[Category("CollectiveEvents")]
[Category("Shard2")]
public class CollectiveReplayMaterializationTests {

  [Test]
  public async Task Interleave_WithAnAsynchronousQueryProvider_FoldsTheCollectiveInAsync() {
    var result = await _interleaveAsync(asynchronous: true);

    await Assert.That(result).Count().IsEqualTo(2);
    await Assert.That(result.Any(e => e.Payload is ProbeCollectiveEvent)).IsTrue()
      .Because("the positions read through the asynchronous sequence name the collective's stream");
  }

  [Test]
  public async Task Interleave_WithAPlainQueryProvider_FoldsTheCollectiveInAsync() {
    var result = await _interleaveAsync(asynchronous: false);

    await Assert.That(result).Count().IsEqualTo(2);
    await Assert.That(result.Any(e => e.Payload is ProbeCollectiveEvent)).IsTrue();
  }

  private static async Task<IReadOnlyList<MessageEnvelope<IEvent>>> _interleaveAsync(bool asynchronous) {
    var collectiveStream = Guid.CreateVersion7();
    var collective = new MessageEnvelope<IEvent>(MessageId.New(), new ProbeCollectiveEvent(), []);
    List<EventStoreRecord> records = [
      new() {
        Id = collective.MessageId.Value,
        StreamId = collectiveStream,
        AggregateId = collectiveStream,
        AggregateType = "Collective",
        Version = 1,
        EventType = TypeNameFormatter.Format(typeof(ProbeCollectiveEvent)),
        EventData = null,
        Metadata = null,
        Scope = new PerspectiveScope(),
        CommitSequence = 1,
      },
    ];
    IQueryable<EventStoreRecord> query = asynchronous
      ? new AsyncQueryable<EventStoreRecord>(records.AsQueryable())
      : records.AsQueryable();
    var entry = new CollectiveApplyEntry(
      ModelType: typeof(ProbeModel),
      EventType: typeof(ProbeCollectiveEvent),
      HandlerType: typeof(ProbeModel),
      MethodName: "Apply",
      ScopeHandling: default,
      SpecKind: default,
      Invoker: (_, _, _) => new object());
    var applier = new CollectiveReplayApplier(
      [entry], new ServiceCollection().BuildServiceProvider(),
      new OneStreamEventStore(collectiveStream, collective), new QueryOver(query), []);
    List<MessageEnvelope<IEvent>> streamEvents = [new MessageEnvelope<IEvent>(MessageId.New(), new ProbeEvent(), [])];

    ProcessingModeAccessor.Current = ProcessingMode.Rebuild;
    try {
      return await applier.InterleaveForReplayAsync(typeof(ProbeModel), streamEvents, CancellationToken.None);
    } finally {
      ProcessingModeAccessor.Current = null;
    }
  }

  private sealed record ProbeModel;

  private sealed record ProbeEvent : IEvent;

  private sealed record ProbeCollectiveEvent : ICollectiveEvent {
    public CollectiveScope Scope { get; init; } = new TenantCollectiveScope("tenant-a");
  }

  private sealed class QueryOver(IQueryable<EventStoreRecord> query) : IEventStoreQuery {
    public IQueryable<EventStoreRecord> Query => query;
    public IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId) => query;
    public IQueryable<EventStoreRecord> GetEventsByType(string eventType) => query;
  }

  /// <summary>An in-memory query that also enumerates asynchronously, as an Entity Framework query does.</summary>
  private sealed class AsyncQueryable<T>(IQueryable<T> inner) : IQueryable<T>, IAsyncEnumerable<T> {
    public Type ElementType => inner.ElementType;
    public Expression Expression => inner.Expression;
    public IQueryProvider Provider => new AsyncProvider(inner.Provider);
    public IEnumerator<T> GetEnumerator() => inner.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) {
      foreach (var item in inner.AsEnumerable()) {
        cancellationToken.ThrowIfCancellationRequested();
        yield return item;
      }
      await Task.CompletedTask;
    }
  }

  private sealed class AsyncProvider(IQueryProvider inner) : IQueryProvider {
    public IQueryable CreateQuery(Expression expression) => inner.CreateQuery(expression);
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
      new AsyncQueryable<TElement>(inner.CreateQuery<TElement>(expression));
    public object? Execute(Expression expression) => inner.Execute(expression);
    public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(expression);
  }

  /// <summary>An event store holding one collective on one stream.</summary>
  private sealed class OneStreamEventStore(Guid collectiveStreamId, MessageEnvelope<IEvent> collective) : IEventStore {
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message,
      CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      System.Linq.AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      System.Linq.AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
      Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) =>
      streamId == collectiveStreamId
        ? _one(collective)
        : System.Linq.AsyncEnumerable.Empty<MessageEnvelope<IEvent>>();

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(
      Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult<List<MessageEnvelope<TMessage>>>([]);

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes,
      CancellationToken cancellationToken = default) =>
      Task.FromResult<List<MessageEnvelope<IEvent>>>([]);

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(0L);

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(
      IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];

    private static async IAsyncEnumerable<MessageEnvelope<IEvent>> _one(MessageEnvelope<IEvent> envelope) {
      await Task.CompletedTask;
      yield return envelope;
    }
  }
}
