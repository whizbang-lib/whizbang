// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Archival;

/// <summary>
/// The event store's archive seam (#1738, WS-3 S9): reads that start at the beginning of a stream read archived events
/// back when the hot store shows a gap there, and an append to a stream whose type restores on append
/// (<see cref="AppendToArchivedPolicy.AutoRestore"/>) restores the stream and is retried once. Innermost in the event store's
/// decorator chain, so every outer decorator (upcasting, sync tracking, security context, append-and-wait) and every
/// reader that goes through <see cref="IEventStore"/> sees archived events exactly as it sees hot ones, on either driver.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gap detection, no lookup on the hot path.</b> A read from the start of a stream (a sequence read from version 0 or 1,
/// an id read or a between-read with no starting event) looks at the archive only when the hot store shows a gap: no hot
/// event at all, or a first hot event past version 1. A prefix archive always removes version 1 (migration 271 refuses one
/// that does not), so an archived stream always shows the gap, and a stream that was never archived never does. A read
/// that resumes after an event never looks at the archive: archiving refuses while a perspective still owes any event of
/// the range, so a resume point is never inside the archive. The rare read that must resume inside the archive, a rewind
/// from a snapshot an archive covers, asks for it explicitly
/// (<see cref="ReadPolymorphicThroughArchiveAsync"/>).
/// </para>
/// <para>
/// A read whose first event the reader skips (a type it does not handle, a reaped ephemeral body) sees its first returned
/// event past version 1 and asks the archive once, which returns nothing for a stream never archived. Archived events at
/// or above the first hot version are never returned, so an event is never returned twice.
/// </para>
/// <para>
/// Archived rows are decoded by the store underneath (<see cref="IEventStore.DeserializeStreamEvents"/>), the same decoder
/// the drain uses, and carry their stream version and commit positions, so a replay orders them with the hot events
/// exactly as it would have before the archive. A typed read returns the archived events stored as its type.
/// </para>
/// </remarks>
/// <param name="inner">The driver's event store.</param>
/// <param name="archive">The registered archive store.</param>
/// <param name="readThrough">Whether reads from the start read through the archive (<c>Whizbang:Archive:ReadThrough</c>).</param>
/// <docs>fundamentals/events/archive-store#read-through</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/ArchiveAwareEventStoreDecoratorTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveReadThroughIntegrationTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchiveReadThroughTests.cs</tests>
public sealed class ArchiveAwareEventStoreDecorator(IEventStore inner, IArchiveStore archive, bool readThrough)
    : ForwardingEventStoreDecorator(inner) {

  /// <summary>
  /// The version every prefix archive removes. A stream's first version is 1, or 0 for a stream a direct append created
  /// before #1913, so a first hot version above this one is a gap at the start of the stream.
  /// </summary>
  public const long FIRST_ARCHIVED_VERSION = 1;

  private readonly IArchiveStore _archive = archive ?? throw new ArgumentNullException(nameof(archive));

  /// <summary>Whether reads from the start read through the archive.</summary>
  public bool ReadThrough { get; } = readThrough;

  // ── Appends: AutoRestore restores inline and retries once ──────────────────────────────────────────

  /// <inheritdoc />
  public override Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) =>
    ArchivedStreamAutoRestore.RunAsync(() => Inner.AppendAsync(streamId, envelope, cancellationToken), () => _archive, cancellationToken);

  /// <inheritdoc />
  public override Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) =>
    ArchivedStreamAutoRestore.RunAsync(() => Inner.AppendAsync(streamId, message, cancellationToken), () => _archive, cancellationToken);

  // ── Reads from the start: read through on a gap ────────────────────────────────────────────────────

  /// <inheritdoc />
  public override IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
    ReadThrough && fromSequence <= FIRST_ARCHIVED_VERSION
      ? _throughGapAsync(Inner.ReadAsync<TMessage>(streamId, fromSequence, cancellationToken),
          (before, ct) => _archivedTypedAsync<TMessage>(streamId, fromSequence, before, ct), cancellationToken)
      : Inner.ReadAsync<TMessage>(streamId, fromSequence, cancellationToken);

  /// <inheritdoc />
  public override IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
      Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
    ReadThrough && fromEventId is null
      ? _throughGapAsync(Inner.ReadAsync<TMessage>(streamId, fromEventId, cancellationToken),
          (before, ct) => _archivedTypedAsync<TMessage>(streamId, 0, before, ct), cancellationToken)
      : Inner.ReadAsync<TMessage>(streamId, fromEventId, cancellationToken);

  /// <inheritdoc />
  public override IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
      Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
    ReadThrough && fromEventId is null
      ? _throughGapAsync(Inner.ReadPolymorphicAsync(streamId, fromEventId, eventTypes, cancellationToken),
          (before, ct) => _archivedAsync(streamId, 0, before, eventTypes, ct), cancellationToken)
      : Inner.ReadPolymorphicAsync(streamId, fromEventId, eventTypes, cancellationToken);

  /// <inheritdoc />
  public override async Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(
      Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) {
    var live = await Inner.GetEventsBetweenAsync<TMessage>(streamId, afterEventId, upToEventId, cancellationToken).ConfigureAwait(false);
    if (!ReadThrough || afterEventId is not null || !_showsAGap(live)) {
      return live;
    }
    var archived = await _archivedTypedAsync<TMessage>(streamId, 0, _firstVersion(live), cancellationToken).ConfigureAwait(false);
    return [.. archived.Where(e => _atOrBefore(e.MessageId.Value, upToEventId)), .. live];
  }

  /// <inheritdoc />
  public override async Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
      Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
    var live = await Inner.GetEventsBetweenPolymorphicAsync(streamId, afterEventId, upToEventId, eventTypes, cancellationToken)
      .ConfigureAwait(false);
    if (!ReadThrough || afterEventId is not null || !_showsAGap(live)) {
      return live;
    }
    var archived = await _archivedAsync(streamId, 0, _firstVersion(live), eventTypes, cancellationToken).ConfigureAwait(false);
    return [.. archived.Where(e => _atOrBefore(e.MessageId.Value, upToEventId)), .. live];
  }

  /// <summary>
  /// The explicit read through the archive: the archived events above <paramref name="afterVersion"/>, then the hot events
  /// after <paramref name="fromEventId"/> that the archive does not hold. Used by a rewind from a snapshot an archive
  /// covers (its <c>archived_through_version</c> is set), the one resumed read whose resume point may be inside the
  /// archive. Reads through whatever <see cref="ReadThrough"/> says: the caller asked for it.
  /// </summary>
  /// <param name="streamId">The stream.</param>
  /// <param name="fromEventId">The snapshot's event: the hot events after it are read.</param>
  /// <param name="afterVersion">The snapshot's version: the archived events above it are read.</param>
  /// <param name="eventTypes">The event types the reader folds.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The events after the snapshot, archived first, in stream order.</returns>
  public override async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicThroughArchiveAsync(
      Guid streamId, Guid? fromEventId, long afterVersion, IReadOnlyList<Type> eventTypes,
      [EnumeratorCancellation] CancellationToken cancellationToken = default) {
    var archived = await _archivedAsync(streamId, afterVersion + 1, beforeVersion: null, eventTypes, cancellationToken)
      .ConfigureAwait(false);
    var seen = new HashSet<Guid>();
    foreach (var envelope in archived) {
      seen.Add(envelope.MessageId.Value);
      yield return envelope;
    }
    await foreach (var envelope in Inner.ReadPolymorphicAsync(streamId, fromEventId, eventTypes, cancellationToken).ConfigureAwait(false)) {
      if (!seen.Contains(envelope.MessageId.Value)) {
        yield return envelope;
      }
    }
  }

  // The hot read, preceded by the archived events below its first version when it shows a gap at the start.
  private static async IAsyncEnumerable<MessageEnvelope<T>> _throughGapAsync<T>(
      IAsyncEnumerable<MessageEnvelope<T>> live,
      Func<long?, CancellationToken, Task<List<MessageEnvelope<T>>>> archived,
      [EnumeratorCancellation] CancellationToken cancellationToken) {
    await using var hot = live.GetAsyncEnumerator(cancellationToken);
    var any = await hot.MoveNextAsync().ConfigureAwait(false);
    if (!any || hot.Current.LocalStreamVersion > FIRST_ARCHIVED_VERSION) {
      foreach (var envelope in await archived(any ? hot.Current.LocalStreamVersion : null, cancellationToken).ConfigureAwait(false)) {
        yield return envelope;
      }
    }
    if (!any) {
      yield break;
    }
    yield return hot.Current;
    while (await hot.MoveNextAsync().ConfigureAwait(false)) {
      yield return hot.Current;
    }
  }

  private static bool _showsAGap<T>(List<MessageEnvelope<T>> live) =>
    live.Count == 0 || live[0].LocalStreamVersion > FIRST_ARCHIVED_VERSION;

  private static long? _firstVersion<T>(List<MessageEnvelope<T>> live) => live.Count == 0 ? null : live[0].LocalStreamVersion;

  // Guid.Empty is "no upper bound", as the store reads it.
  private static bool _atOrBefore(Guid eventId, Guid upToEventId) => upToEventId == Guid.Empty || eventId.CompareTo(upToEventId) <= 0;

  private async Task<List<MessageEnvelope<TMessage>>> _archivedTypedAsync<TMessage>(
      Guid streamId, long fromVersion, long? beforeVersion, CancellationToken cancellationToken) {
    var archived = await _archivedAsync(streamId, fromVersion, beforeVersion, [typeof(TMessage)], cancellationToken).ConfigureAwait(false);
    return archived.ConvertAll(e => _retyped<TMessage>(e));
  }

  // The archive's events from fromVersion and below beforeVersion (when given), decoded by the store underneath and
  // carrying their stream position.
  private async Task<List<MessageEnvelope<IEvent>>> _archivedAsync(
      Guid streamId, long fromVersion, long? beforeVersion, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken) {
    var rows = new List<StreamEventData>();
    var positions = new Dictionary<Guid, ArchivedEvent>();
    await foreach (var archived in _archive.ReadAsync(streamId, fromVersion, cancellationToken).ConfigureAwait(false)) {
      if (archived.Version >= beforeVersion) {
        break;
      }
      // A row with no body held no event a reader could fold, as a reaped body on the hot side.
      if (archived.EventDataJson is null) {
        continue;
      }
      positions[archived.EventId] = archived;
      rows.Add(new StreamEventData {
        StreamId = archived.StreamId,
        EventId = archived.EventId,
        EventType = archived.EventType,
        EventData = archived.EventDataJson,
        Metadata = archived.MetadataJson,
        Scope = archived.ScopeJson,
        EventWorkId = Guid.Empty,
        CommitSequence = archived.CommitSequence,
      });
    }
    if (rows.Count == 0) {
      return [];
    }
    return Inner.DeserializeStreamEvents(rows, eventTypes).ConvertAll(e => _positioned(e, positions));
  }

  // The decoded envelope with the stream position the hot read gives it, so a replay orders archived and hot events alike.
  private static MessageEnvelope<IEvent> _positioned(MessageEnvelope<IEvent> envelope, Dictionary<Guid, ArchivedEvent> positions) {
    if (!positions.TryGetValue(envelope.MessageId.Value, out var archived)) {
      return envelope;
    }
    return new MessageEnvelope<IEvent> {
      Version = envelope.Version,
      DispatchContext = envelope.DispatchContext,
      MessageId = envelope.MessageId,
      Payload = envelope.Payload,
      Hops = envelope.Hops,
      SourceServiceId = archived.OriginServiceId ?? Guid.Empty,
      SourceCommitSequence = archived.OriginCommitSequence ?? 0,
      LocalCommitSequence = archived.CommitSequence,
      LocalStreamVersion = archived.Version,
      Flags = envelope.Flags,
    };
  }

  private static MessageEnvelope<TMessage> _retyped<TMessage>(MessageEnvelope<IEvent> envelope) => new() {
    Version = envelope.Version,
    DispatchContext = envelope.DispatchContext,
    MessageId = envelope.MessageId,
    Payload = (TMessage)(object)envelope.Payload,
    Hops = envelope.Hops,
    SourceServiceId = envelope.SourceServiceId,
    SourceCommitSequence = envelope.SourceCommitSequence,
    LocalCommitSequence = envelope.LocalCommitSequence,
    LocalStreamVersion = envelope.LocalStreamVersion,
    Flags = envelope.Flags,
  };
}
