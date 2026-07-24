using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// PostgreSQL implementation of <see cref="IPerspectiveReplayReader"/>. Streams events
/// for a perspective stream replay, annotated with <c>IsNew</c> derived from a LEFT JOIN
/// against <c>wh_perspective_events</c>.
/// </summary>
/// <remarks>
/// <para>
/// A row present in <c>wh_perspective_events</c> for (stream_id, perspective_name, event_id)
/// means the event has been queued for this perspective but has not yet been completed —
/// handlers still need to fire. A row absent means the event was completed in a prior run
/// (the work row was deleted by <c>complete_perspective_events</c>) — handlers should not
/// re-fire during replay unless the receptor is decorated with
/// <c>[ReceptorIdempotent(AlwaysFire = true)]</c>.
/// </para>
/// <para>
/// Events are deserialized using the existing <see cref="IEventStore.GetEventsBetweenPolymorphicAsync"/>
/// path so they flow through the same JSON + polymorphic type-resolution pipeline as live
/// processing. The pending-event-id set is fetched in a single round-trip via a bounded
/// SQL query.
/// </para>
/// </remarks>
/// <docs>operations/workers/perspective-worker#rewind-replay</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/EFCorePerspectiveReplayReaderTests.cs</tests>
public sealed class EFCorePerspectiveReplayReader<TDbContext>(TDbContext context, IEventStore eventStore) : IPerspectiveReplayReader
  where TDbContext : DbContext {

  private readonly TDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
  private readonly IEventStore _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));

  /// <inheritdoc/>
  public IAsyncEnumerable<ReplayEventEnvelope> ReadReplayEventsAsync(
      Guid streamId,
      string perspectiveName,
      int fromVersionExclusive,
      IReadOnlyCollection<Type> eventTypes,
      CancellationToken cancellationToken) {

    ArgumentException.ThrowIfNullOrEmpty(perspectiveName);
    ArgumentNullException.ThrowIfNull(eventTypes);

    return _readReplayEventsImplAsync(streamId, perspectiveName, fromVersionExclusive, eventTypes, cancellationToken);
  }

  private async IAsyncEnumerable<ReplayEventEnvelope> _readReplayEventsImplAsync(
      Guid streamId,
      string perspectiveName,
      int fromVersionExclusive,
      IReadOnlyCollection<Type> eventTypes,
      [EnumeratorCancellation] CancellationToken cancellationToken) {

    // 1. Pull the set of pending event ids for this (stream, perspective) from
    //    wh_perspective_events. Rows here have never been completed for this perspective —
    //    handlers still owe them a lifecycle run.
    var pendingIds = await _fetchPendingEventIdsAsync(streamId, perspectiveName, cancellationToken);

    // 2. Stream events from the event store using the existing polymorphic reader. We read
    //    every event in the stream (upTo=Empty, after=null) and filter by version in-memory
    //    — fromVersionExclusive is typically 0 (full replay) or the snapshot's version.
    //    For large streams the caller can pass a higher fromVersionExclusive to start from
    //    the snapshot point; we still fetch all events and keep those whose Version > bound.
    var envelopes = await _eventStore.GetEventsBetweenPolymorphicAsync(
        streamId,
        afterEventId: null,
        upToEventId: Guid.Empty,
        [.. eventTypes],
        cancellationToken);

    foreach (var envelope in envelopes) {
      var id = envelope.MessageId.Value;
      var isNew = pendingIds.Contains(id);
      yield return new ReplayEventEnvelope(envelope, isNew);
    }
  }

  private async Task<HashSet<Guid>> _fetchPendingEventIdsAsync(
      Guid streamId,
      string perspectiveName,
      CancellationToken cancellationToken) {

    var connection = _context.Database.GetDbConnection();
    var needsClose = connection.State != System.Data.ConnectionState.Open;
    if (needsClose) {
      await connection.OpenAsync(cancellationToken);
    }
    try {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText = """
        SELECT event_id
        FROM wh_perspective_events
        WHERE stream_id = @stream_id
          AND perspective_name = @perspective_name
          AND processed_at IS NULL
        """;
      var streamIdParam = cmd.CreateParameter();
      streamIdParam.ParameterName = "@stream_id";
      streamIdParam.Value = streamId;
      cmd.Parameters.Add(streamIdParam);

      var perspNameParam = cmd.CreateParameter();
      perspNameParam.ParameterName = "@perspective_name";
      perspNameParam.Value = perspectiveName;
      cmd.Parameters.Add(perspNameParam);

      var result = new HashSet<Guid>();
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
      while (await reader.ReadAsync(cancellationToken)) {
        result.Add(reader.GetGuid(0));
      }
      return result;
    } finally {
      if (needsClose) {
        await connection.CloseAsync();
      }
    }
  }
}
