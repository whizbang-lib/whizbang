// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// A collective replay on the Dapper driver finds the collective streams archived whole (#1932). Such a stream has no
/// hot row, so the replay lists it from the stream lifecycle ledger (state Archived, a collective type, the replayed
/// stream's tenant) over the database connection the driver registers for archive work, and reads it through the event
/// store, whose archive seam serves its events. A rebuild does so only when it includes streams archived whole (#1931).
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
public class DapperArchivedCollectiveReplayTests : PostgresTestBase {
  private const string TENANT = "tenant-a";

  [Test]
  public async Task AddCollectiveEventsDapper_TheReplayReadsTheTenantsArchivedCollectiveStreams_ListedFromTheLedgerAsync() {
    var ownType = (Guid)TrackedGuid.New();
    var otherModelsType = (Guid)TrackedGuid.New();
    var otherTenant = (Guid)TrackedGuid.New();
    var stillActive = (Guid)TrackedGuid.New();
    var notACollective = (Guid)TrackedGuid.New();
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync();
      await _ledgerRowAsync(conn, ownType, typeof(ResetCollective), TENANT, "Archived");
      // A keyed stream is shared by every collective of its key, and its type is its first event's: another model's.
      await _ledgerRowAsync(conn, otherModelsType, typeof(OtherModelCollective), TENANT, "Archived");
      await _ledgerRowAsync(conn, otherTenant, typeof(ResetCollective), "tenant-b", "Archived");
      await _ledgerRowAsync(conn, stillActive, typeof(ResetCollective), TENANT, "Active");
      await _ledgerRowAsync(conn, notACollective, typeof(PlainEvent), TENANT, "Archived");
    }
    var reads = new RecordingEventStore();
    var services = new ServiceCollection().AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), false, (string?)null, null);
    services.AddSingleton<IEventStore>(reads);
    services.AddSingleton<IEventStoreQuery>(new NoHotEvents());
    services.AddCollectiveEventsDapper([
      _entry(typeof(ProbeModel), typeof(ResetCollective)),
      _entry(typeof(OtherModel), typeof(OtherModelCollective)),
    ]);
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    var applier = scope.ServiceProvider.GetRequiredService<ICollectiveReplayApplier>();

    // A rebuild that includes streams archived whole (#1931); the default leaves them out.
    ProcessingModeAccessor.Current = ProcessingMode.Rebuild;
    RebuildArchivedDataAccessor.Current = RebuildArchivedData.IncludeAll;
    try {
      _ = await applier.InterleaveForReplayAsync(typeof(ProbeModel), _tenantEvent(), CancellationToken.None);
    } finally {
      ProcessingModeAccessor.Current = null;
      RebuildArchivedDataAccessor.Current = RebuildArchivedData.Skip;
    }

    await Assert.That(reads.Streams).IsEquivalentTo([ownType, otherModelsType])
      .Because("the tenant's archived streams of any registered collective type are read through the event store; "
        + "another tenant's, a stream that is not archived and a stream of another type are not");
  }

  // ===== Fixtures =====

  private static async Task _ledgerRowAsync(NpgsqlConnection conn, Guid stream, Type firstEvent, string tenant, string state) =>
    _ = await conn.ExecuteAsync(
      "INSERT INTO wh_stream_lifecycle (stream_id, stream_type, tenant, first_appended_at, last_appended_bucket, last_version, state) " +
      "VALUES (@stream, @type, @tenant, NOW(), date_trunc('hour', NOW()), 1, @state)",
      new { stream, type = firstEvent.FullName, tenant, state });

  private static CollectiveApplyEntry _entry(Type model, Type collective) => new(
    ModelType: model,
    EventType: collective,
    HandlerType: typeof(object),
    MethodName: "Apply",
    ScopeHandling: default,
    SpecKind: default,
    Invoker: (_, _, _) => new object());

  // One event of the replayed stream, carrying its tenant.
  private static List<MessageEnvelope<IEvent>> _tenantEvent() => [
    new(MessageId.New(), new PlainEvent(), [
      new MessageHop {
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Type = HopType.Current,
        Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Scope = Whizbang.Core.Security.ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = TENANT }),
      },
    ]),
  ];

  private sealed record ProbeModel;

  private sealed record OtherModel;

  private sealed record ResetCollective : ICollectiveEvent {
    public CollectiveScope Scope { get; init; } = new TenantCollectiveScope(TENANT);
  }

  private sealed record OtherModelCollective : ICollectiveEvent {
    public CollectiveScope Scope { get; init; } = new TenantCollectiveScope(TENANT);
  }

  private sealed record PlainEvent : IEvent;

  // The hot store holds no collective: every stream the replay reads came from the ledger.
  private sealed class NoHotEvents : IEventStoreQuery {
    public IQueryable<EventStoreRecord> Query => Array.Empty<EventStoreRecord>().AsQueryable();
    public IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId) => Query;
    public IQueryable<EventStoreRecord> GetEventsByType(string eventType) => Query;
  }

  // Records every stream the replay reads; holds no events.
  private sealed class RecordingEventStore : IEventStore {
    private readonly ConcurrentQueue<Guid> _streams = new();
    public IReadOnlyList<Guid> Streams => [.. _streams];

    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
        Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      _streams.Enqueue(streamId);
      return System.Linq.AsyncEnumerable.Empty<MessageEnvelope<IEvent>>();
    }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull =>
      throw new NotSupportedException();

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(
        Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
        Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      throw new NotSupportedException();
  }
}
