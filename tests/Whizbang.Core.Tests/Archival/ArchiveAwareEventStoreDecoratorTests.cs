// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Archival;

/// <summary>A probe event: its number is its position in the stream it was written to.</summary>
public sealed record ArchiveProbeEvent : IEvent {
  /// <summary>The event's position in the stream it was written to.</summary>
  public int N { get; init; }

  /// <summary>The stream.</summary>
  [StreamId]
  public Guid StreamId { get; init; }
}

/// <summary>Another probe type, for the reads that fold only some types.</summary>
public sealed record ArchiveProbeOtherEvent : IEvent {
  /// <summary>The event's position in the stream it was written to.</summary>
  public int N { get; init; }

  /// <summary>The stream.</summary>
  [StreamId]
  public Guid StreamId { get; init; }
}

/// <summary>
/// The event store's archive seam (#1738, WS-3 S9). A read from the start of a stream looks at the archive only when the
/// hot store shows a gap there (no hot event, or a first hot event past version 1); a read that resumes after an event
/// never does; the explicit read through the archive (a rewind from a snapshot an archive covers) reads the archive above
/// the snapshot and then the hot events after it; and an append under <see cref="AppendToArchivedPolicy.AutoRestore"/>
/// restores the stream and is retried once.
/// </summary>
/// <docs>fundamentals/events/archive-store#read-through</docs>
public class ArchiveAwareEventStoreDecoratorTests {
  private static readonly Guid _stream = Guid.Parse("0199f0a1-0000-7000-8000-0000000000aa");
  private static readonly IReadOnlyList<Type> _types = [typeof(ArchiveProbeEvent)];

  // ===== Reads that resume never look at the archive =====

  [Test]
  public async Task AReadThatResumesAfterAnEvent_NeverReadsTheArchive_OnAnyReadPathAsync() {
    var (hot, archive, store) = _archivedPrefix(archivedThrough: 3, hotThrough: 5);
    var resumeAt = hot.Hot[0].MessageId.Value;

    _ = await _listAsync(store.ReadPolymorphicAsync(_stream, resumeAt, _types));
    _ = await _listAsync(store.ReadAsync<ArchiveProbeEvent>(_stream, resumeAt));
    _ = await _listAsync(store.ReadAsync<ArchiveProbeEvent>(_stream, fromSequence: 2));
    _ = await store.GetEventsBetweenAsync<ArchiveProbeEvent>(_stream, resumeAt, Guid.Empty);
    _ = await store.GetEventsBetweenPolymorphicAsync(_stream, resumeAt, Guid.Empty, _types);

    await Assert.That(archive.Reads).IsEqualTo(0)
      .Because("a perspective catching up resumes after an event it applied, which archiving never moves: zero added cost");
  }

  [Test]
  public async Task AReadFromTheStart_OfAStreamWithNoGap_NeverReadsTheArchiveAsync() {
    foreach (var firstVersion in new long[] { 0, 1 }) {
      var hot = new HotStore();
      hot.Add(_stream, firstVersion, firstVersion + 1);
      var archive = new ArchiveFake();
      var store = new ArchiveAwareEventStoreDecorator(hot, archive, readThrough: true);

      _ = await _listAsync(store.ReadPolymorphicAsync(_stream, null, _types));
      _ = await _listAsync(store.ReadAsync<ArchiveProbeEvent>(_stream, (Guid?)null));
      _ = await _listAsync(store.ReadAsync<ArchiveProbeEvent>(_stream, fromSequence: 0));
      _ = await store.GetEventsBetweenAsync<ArchiveProbeEvent>(_stream, null, Guid.Empty);
      _ = await store.GetEventsBetweenPolymorphicAsync(_stream, null, Guid.Empty, _types);

      await Assert.That(archive.Reads).IsEqualTo(0)
        .Because($"a stream whose first hot version is {firstVersion} starts where every stream starts: there is no gap");
    }
  }

  [Test]
  public async Task AHotReadThatCarriesNoVersions_NeverReadsTheArchiveAsync() {
    var hot = new HotStore { StampVersions = false };
    hot.Add(_stream, 4, 5);
    var archive = new ArchiveFake();
    archive.Add(_stream, 1, 3);
    var store = new ArchiveAwareEventStoreDecorator(hot, archive, readThrough: true);

    var read = await _listAsync(store.ReadPolymorphicAsync(_stream, null, _types));

    await Assert.That(read.Select(_n)).IsEquivalentTo([4, 5]);
    await Assert.That(archive.Reads).IsEqualTo(0).Because("a gap the store cannot show is not assumed");
  }

  // ===== Reads from the start read through a gap =====

  [Test]
  public async Task AReadFromTheStart_PastAGap_ReturnsTheArchivedEventsFirst_ThenTheHotOnes_OnEveryReadPathAsync() {
    var (_, archive, store) = _archivedPrefix(archivedThrough: 3, hotThrough: 5);

    await Assert.That((await _listAsync(store.ReadPolymorphicAsync(_stream, null, _types))).Select(_n)).IsEquivalentTo([1, 2, 3, 4, 5]);
    await Assert.That((await _listAsync(store.ReadAsync<ArchiveProbeEvent>(_stream, (Guid?)null))).Select(_n)).IsEquivalentTo([1, 2, 3, 4, 5]);
    await Assert.That((await _listAsync(store.ReadAsync<ArchiveProbeEvent>(_stream, fromSequence: 1))).Select(_n)).IsEquivalentTo([1, 2, 3, 4, 5]);
    await Assert.That((await store.GetEventsBetweenAsync<ArchiveProbeEvent>(_stream, null, Guid.Empty)).Select(_n)).IsEquivalentTo([1, 2, 3, 4, 5]);
    await Assert.That((await store.GetEventsBetweenPolymorphicAsync(_stream, null, Guid.Empty, _types)).Select(_n)).IsEquivalentTo([1, 2, 3, 4, 5]);
    await Assert.That(archive.Reads).IsEqualTo(5).Because("one archive read per read that showed the gap");
  }

  [Test]
  public async Task AReadFromTheStart_OfAStreamArchivedWhole_ReturnsTheWholeArchiveAsync() {
    var hot = new HotStore();
    var archive = new ArchiveFake();
    archive.Add(_stream, 1, 4);
    var store = new ArchiveAwareEventStoreDecorator(hot, archive, readThrough: true);

    await Assert.That((await _listAsync(store.ReadPolymorphicAsync(_stream, null, _types))).Select(_n)).IsEquivalentTo([1, 2, 3, 4]);
    await Assert.That((await store.GetEventsBetweenPolymorphicAsync(_stream, null, Guid.Empty, _types)).Select(_n)).IsEquivalentTo([1, 2, 3, 4]);
  }

  [Test]
  public async Task ArchivedEvents_AtOrAboveTheFirstHotVersion_AreNeverReturnedTwiceAsync() {
    var hot = new HotStore();
    hot.Add(_stream, 3, 4);
    var archive = new ArchiveFake();
    // A restore in flight: the archive still holds version 3, which is hot again.
    archive.Add(_stream, 1, 3);
    archive.Events[^1] = archive.Events[^1] with { EventId = hot.Hot[0].MessageId.Value };
    var store = new ArchiveAwareEventStoreDecorator(hot, archive, readThrough: true);

    await Assert.That((await _listAsync(store.ReadPolymorphicAsync(_stream, null, _types))).Select(_n)).IsEquivalentTo([1, 2, 3, 4]);
  }

  [Test]
  public async Task AnArchivedEvent_CarriesItsStreamPosition_SoAReplayOrdersItWithTheHotOnesAsync() {
    var (_, _, store) = _archivedPrefix(archivedThrough: 2, hotThrough: 3);

    var read = await _listAsync(store.ReadPolymorphicAsync(_stream, null, _types));

    await Assert.That(read[0].LocalStreamVersion).IsEqualTo(1L);
    await Assert.That(read[0].LocalCommitSequence).IsEqualTo(101L);
    await Assert.That(read[0].SourceServiceId).IsEqualTo(ArchiveFake.Origin);
    await Assert.That(read[0].SourceCommitSequence).IsEqualTo(1001L);
    await Assert.That(read[1].LocalStreamVersion).IsEqualTo(2L);
  }

  [Test]
  public async Task AnArchivedRowWithoutABody_OrOfATypeTheReadDoesNotFold_IsSkippedAsync() {
    var hot = new HotStore();
    var archive = new ArchiveFake();
    archive.Add(_stream, 1, 3);
    archive.Events[1] = archive.Events[1] with { EventDataJson = null };
    archive.Events[2] = archive.Events[2] with { EventType = nameof(ArchiveProbeOtherEvent) };
    var store = new ArchiveAwareEventStoreDecorator(hot, archive, readThrough: true);

    await Assert.That((await _listAsync(store.ReadPolymorphicAsync(_stream, null, _types))).Select(_n)).IsEquivalentTo([1]);
  }

  [Test]
  public async Task ABetweenRead_WithAnUpperBound_KeepsTheArchivedEventsAtOrBeforeItAsync() {
    var (_, archive, store) = _archivedPrefix(archivedThrough: 3, hotThrough: 4);
    var upTo = archive.Events[1].EventId;

    var read = await store.GetEventsBetweenPolymorphicAsync(_stream, null, upTo, _types);
    var typed = await store.GetEventsBetweenAsync<ArchiveProbeEvent>(_stream, null, upTo);

    await Assert.That(read.Select(_n)).IsEquivalentTo([1, 2]).Because("the bound is an event id, as the hot read applies it");
    await Assert.That(typed.Select(_n)).IsEquivalentTo([1, 2]);
  }

  [Test]
  public async Task ReadThroughOff_ReturnsOnlyTheHotEvents_AndNeverReadsTheArchiveAsync() {
    var hot = new HotStore();
    hot.Add(_stream, 3, 4);
    var archive = new ArchiveFake();
    archive.Add(_stream, 1, 2);
    var store = new ArchiveAwareEventStoreDecorator(hot, archive, readThrough: false);

    await Assert.That((await _listAsync(store.ReadPolymorphicAsync(_stream, null, _types))).Select(_n)).IsEquivalentTo([3, 4]);
    await Assert.That((await _listAsync(store.ReadAsync<ArchiveProbeEvent>(_stream, fromSequence: 0))).Select(_n)).IsEquivalentTo([3, 4]);
    await Assert.That((await store.GetEventsBetweenPolymorphicAsync(_stream, null, Guid.Empty, _types)).Select(_n)).IsEquivalentTo([3, 4]);
    await Assert.That((await store.GetEventsBetweenAsync<ArchiveProbeEvent>(_stream, null, Guid.Empty)).Select(_n)).IsEquivalentTo([3, 4]);
    await Assert.That(archive.Reads).IsEqualTo(0);
    await Assert.That(store.ReadThrough).IsFalse();
  }

  // ===== The explicit read through the archive =====

  [Test]
  public async Task ThroughArchive_FromASnapshot_ReadsTheArchivedEventsAboveItsVersion_ThenTheHotEventsAfterItAsync() {
    var (hot, archive, store) = _archivedPrefix(archivedThrough: 4, hotThrough: 6);
    var snapshotEvent = archive.Events[1].EventId;

    var read = await _listAsync(store.ReadPolymorphicThroughArchiveAsync(_stream, snapshotEvent, afterVersion: 2, _types));

    await Assert.That(read.Select(_n)).IsEquivalentTo([3, 4, 5, 6])
      .Because("the snapshot holds everything through version 2; versions 3 and 4 are archived, 5 and 6 hot");
    await Assert.That(hot.LastFromEventId).IsEqualTo(snapshotEvent);
  }

  [Test]
  public async Task ThroughArchive_AnEventBothArchivedAndHot_IsReturnedOnceAsync() {
    var hot = new HotStore();
    hot.Add(_stream, 3, 4);
    var archive = new ArchiveFake();
    archive.Add(_stream, 1, 3);
    archive.Events[^1] = archive.Events[^1] with { EventId = hot.Hot[0].MessageId.Value };
    var store = new ArchiveAwareEventStoreDecorator(hot, archive, readThrough: false);

    var read = await _listAsync(store.ReadPolymorphicThroughArchiveAsync(_stream, null, afterVersion: 1, _types));

    await Assert.That(read.Select(_n)).IsEquivalentTo([2, 3, 4])
      .Because("the caller asked for the archive explicitly, so the read-through setting does not apply");
  }

  [Test]
  public async Task TheInterfaceDefault_ForAStoreWithNoArchive_IsTheOrdinaryReadAsync() {
    var hot = new HotStore();
    hot.Add(_stream, 1, 3);
    var read = await _listAsync(((IEventStore)hot).ReadPolymorphicThroughArchiveAsync(_stream, hot.Hot[0].MessageId.Value, afterVersion: 1, _types));

    await Assert.That(read.Select(_n)).IsEquivalentTo([2, 3]);
  }

  // ===== Appends under AutoRestore =====

  [Test]
  public async Task AnAppend_AutoRestore_RestoresTheStream_AndIsRetriedOnceAsync() {
    var hot = new HotStore();
    hot.AppendFailures.Enqueue(_refusal(AppendToArchivedPolicy.AutoRestore));
    var archive = new ArchiveFake();
    var store = new ArchiveAwareEventStoreDecorator(hot, archive, readThrough: true);

    await store.AppendAsync(_stream, new ArchiveProbeEvent { N = 9 });

    await Assert.That(archive.Restores).IsEquivalentTo([_stream]);
    await Assert.That(hot.Appends).IsEqualTo(2).Because("refused, then stored once the stream is back");
  }

  [Test]
  public async Task AnEnvelopeAppend_AutoRestore_RestoresTheStream_AndIsRetriedOnceAsync() {
    var hot = new HotStore();
    hot.AppendFailures.Enqueue(_refusal(AppendToArchivedPolicy.AutoRestore));
    var archive = new ArchiveFake();
    var store = new ArchiveAwareEventStoreDecorator(hot, archive, readThrough: true);

    await store.AppendAsync(_stream, HotStore.Envelope(9, 1));

    await Assert.That(archive.Restores).IsEquivalentTo([_stream]);
    await Assert.That(hot.Appends).IsEqualTo(2);
  }

  [Test]
  public async Task AnAppend_AutoRestore_WhoseRestoreDoesNotHelp_RaisesTheSecondRefusal_AfterOneRetryAsync() {
    var hot = new HotStore();
    var second = _refusal(AppendToArchivedPolicy.AutoRestore);
    hot.AppendFailures.Enqueue(_refusal(AppendToArchivedPolicy.AutoRestore));
    hot.AppendFailures.Enqueue(second);
    var archive = new ArchiveFake();
    var store = new ArchiveAwareEventStoreDecorator(hot, archive, readThrough: true);

    var thrown = await Assert.That(async () => await store.AppendAsync(_stream, new ArchiveProbeEvent { N = 9 })).Throws<StreamArchivedException>();

    await Assert.That(thrown).IsSameReferenceAs(second);
    await Assert.That(archive.Restores).Count().IsEqualTo(1).Because("retried once, never in a loop");
    await Assert.That(hot.Appends).IsEqualTo(2);
  }

  [Test]
  public async Task AnAppend_Refuse_IsRaised_AndNothingIsRestoredAsync() {
    var hot = new HotStore();
    hot.AppendFailures.Enqueue(_refusal(AppendToArchivedPolicy.Refuse));
    var archive = new ArchiveFake();
    var store = new ArchiveAwareEventStoreDecorator(hot, archive, readThrough: true);

    await Assert.That(async () => await store.AppendAsync(_stream, new ArchiveProbeEvent { N = 9 })).Throws<StreamArchivedException>();

    await Assert.That(archive.Restores).IsEmpty();
    await Assert.That(hot.Appends).IsEqualTo(1);
  }

  [Test]
  public async Task TheDecorator_RefusesNoArchiveStoreAsync() {
    await Assert.That(() => new ArchiveAwareEventStoreDecorator(new HotStore(), null!, readThrough: true))
      .Throws<ArgumentNullException>();
  }

  // =================================================================================================

  private static StreamArchivedException _refusal(AppendToArchivedPolicy policy) =>
    new($"Stream {_stream} is archived through version 3", _stream, 3, policy);

  private static int _n<T>(MessageEnvelope<T> envelope) => envelope.Payload switch {
    ArchiveProbeEvent e => e.N,
    _ => -1,
  };

  private static (HotStore Hot, ArchiveFake Archive, ArchiveAwareEventStoreDecorator Store) _archivedPrefix(int archivedThrough, int hotThrough) {
    // The archived events were written first, so their ids sort before the hot ones', as in a real stream.
    var archive = new ArchiveFake();
    archive.Add(_stream, 1, archivedThrough);
    var hot = new HotStore();
    hot.Add(_stream, archivedThrough + 1, hotThrough);
    return (hot, archive, new ArchiveAwareEventStoreDecorator(hot, archive, readThrough: true));
  }

  private static async Task<List<T>> _listAsync<T>(IAsyncEnumerable<T> source) {
    var list = new List<T>();
    await foreach (var item in source) {
      list.Add(item);
    }
    return list;
  }

  /// <summary>An archive store holding a list of archived events, counting reads and restores.</summary>
  internal sealed class ArchiveFake : IArchiveStore {
    public static readonly Guid Origin = Guid.Parse("0199f0a1-0000-7000-8000-0000000000ff");
    public List<ArchivedEvent> Events { get; } = [];
    public int Reads { get; private set; }
    public List<Guid> Restores { get; } = [];

    public void Add(Guid stream, int from, int through) {
      for (var n = from; n <= through; n++) {
        Events.Add(new ArchivedEvent(TrackedGuid.New().Value, stream, n, nameof(ArchiveProbeEvent), $$"""{"n":{{n}}}""", "{}") {
          CommitSequence = 100 + n,
          OriginServiceId = Origin,
          OriginCommitSequence = 1000 + n,
        });
      }
    }

    public Task<ArchiveWriteResult> WriteAsync(ArchiveBatch batch, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public async IAsyncEnumerable<ArchivedEvent> ReadAsync(Guid streamId, long fromVersion = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
      Reads++;
      foreach (var e in Events.Where(e => e.StreamId == streamId && e.Version >= fromVersion).OrderBy(e => e.Version)) {
        await Task.Yield();
        yield return e;
      }
    }

    public Task<ArchiveRestoreResult> RestoreAsync(Guid streamId, CancellationToken cancellationToken = default) {
      Restores.Add(streamId);
      return Task.FromResult(new ArchiveRestoreResult(ArchiveStatus.RESTORED, 0, 1));
    }

    public Task<long> DeleteAsync(Guid streamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
  }

  /// <summary>
  /// A hot store over a list of envelopes, versioned like the Postgres drivers, decoding archived rows the way their
  /// <see cref="IEventStore.DeserializeStreamEvents"/> does: by the type the reader folds.
  /// </summary>
  internal sealed class HotStore : IEventStore {
    public List<MessageEnvelope<IEvent>> Hot { get; } = [];
    public Queue<Exception> AppendFailures { get; } = new();
    public int Appends { get; private set; }
    public bool StampVersions { get; init; } = true;
    public Guid? LastFromEventId { get; private set; }

    public static MessageEnvelope<IEvent> Envelope(int n, long? version) => new() {
      MessageId = MessageId.New(),
      Payload = new ArchiveProbeEvent { N = n },
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      LocalStreamVersion = version,
    };

    public void Add(Guid stream, long from, long through) {
      _ = stream;
      for (var v = from; v <= through; v++) {
        Hot.Add(Envelope((int)v, StampVersions ? v : null));
      }
    }

    private IEnumerable<MessageEnvelope<IEvent>> _after(Guid? fromEventId, Guid upToEventId = default) =>
      Hot.Where(e => (fromEventId is not { } from || e.MessageId.Value.CompareTo(from) > 0)
                     && (upToEventId == Guid.Empty || e.MessageId.Value.CompareTo(upToEventId) <= 0));

    private Task _appendAsync() {
      Appends++;
      return AppendFailures.TryDequeue(out var failure) ? Task.FromException(failure) : Task.CompletedTask;
    }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => _appendAsync();
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => _appendAsync();

    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
      foreach (var e in Hot.Where(e => e.LocalStreamVersion is null || e.LocalStreamVersion >= fromSequence)) {
        await Task.Yield();
        yield return _typed<TMessage>(e);
      }
    }

    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
      foreach (var e in _after(fromEventId)) {
        await Task.Yield();
        yield return _typed<TMessage>(e);
      }
    }

    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
      LastFromEventId = fromEventId;
      foreach (var e in _after(fromEventId)) {
        await Task.Yield();
        yield return e;
      }
    }

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(_after(afterEventId, upToEventId).Select(_typed<TMessage>).ToList());

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(_after(afterEventId, upToEventId).ToList());

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(-1L);

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      [.. streamEvents
        .Where(r => eventTypes.Any(t => t.Name == r.EventType))
        .Select(r => new MessageEnvelope<IEvent> {
          MessageId = MessageId.From(r.EventId),
          Payload = new ArchiveProbeEvent { N = JsonElement.Parse(r.EventData).GetProperty("n").GetInt32() },
          Hops = [],
          DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local },
          LocalCommitSequence = r.CommitSequence,
        })];

    private static MessageEnvelope<TMessage> _typed<TMessage>(MessageEnvelope<IEvent> e) => new() {
      MessageId = e.MessageId,
      Payload = (TMessage)(object)e.Payload,
      Hops = e.Hops,
      DispatchContext = e.DispatchContext,
      LocalStreamVersion = e.LocalStreamVersion,
    };
  }
}
