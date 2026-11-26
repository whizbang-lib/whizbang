// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A rebuild meets the write-behind mode (#2533): it flushes the models held for the perspective it rebuilds (every one
/// of them, or those of the streams it names) before it reads a row, so no model held before the rebuild is written over
/// it afterwards.
/// </summary>
/// <remarks>
/// The rebuild runs through <see cref="PerspectiveRebuilder"/>, the entry point every rebuild takes, against the rig's
/// worker. What the rebuild saw is recorded as it reads each row, so every assertion is about that moment, not a later
/// one; the flush completes before the rebuild reads anything, so no wait is needed beyond the rebuild itself.
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#write-behind</docs>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.WriteBehind.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRebuilder.cs</code-under-test>
public partial class PerspectiveWorkerDrainModeTests {
  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_ARebuildOfAHeldStream_FlushesItsModelBeforeTheRebuildReadsItsRowAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    var rebuilt = Guid.NewGuid();
    var other = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);
    await rig.DrainAsync(rebuilt, 1, cancellationToken);
    await rig.DrainAsync(other, 2, cancellationToken);
    var reads = _recordRowReads(rig);

    var result = await _rebuilderFor(rig, []).RebuildStreamsAsync(BEHIND, [rebuilt], cancellationToken);

    await Assert.That(result.Success).IsTrue();
    var read = reads.Single();
    await Assert.That(read.StreamId).IsEqualTo(rebuilt);
    await Assert.That(read.Written).IsEquivalentTo([rebuilt])
      .Because("the model held before the rebuild is written before the rebuild reads its row, never over it afterwards");
    await Assert.That(read.Held).IsEqualTo(1).Because("a rebuild of named streams leaves the models of the others held");
    await Assert.That(rig.Runners.Completed.Select(c => c.Status)).IsEquivalentTo([(PerspectiveWriteStatus?)PerspectiveWriteStatus.Written])
      .Because("the flushed batch is completed as any flush completes it");
    await Assert.That(rig.Measured("whizbang.perspective.write_behind.forced_flushes", "rebuild")).IsEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_ARebuildOfEveryStream_FlushesEveryModelOfThePerspectiveBeforeItReadsARowAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    var first = Guid.NewGuid();
    var second = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);
    await rig.DrainAsync(first, 1, cancellationToken);
    await rig.DrainAsync(second, 2, cancellationToken);
    var reads = _recordRowReads(rig);

    var result = await _rebuilderFor(rig, [first, second]).RebuildInPlaceAsync(BEHIND, cancellationToken);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(reads).Count().IsEqualTo(2);
    await Assert.That(reads.First().Written).IsEquivalentTo([first, second])
      .Because("a rebuild of every stream flushes every model the perspective holds before it reads its first row");
    await Assert.That(reads.First().Held).IsEqualTo(0);
    await Assert.That(rig.Measured("whizbang.perspective.write_behind.forced_flushes", "rebuild")).IsEqualTo(2);
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_ARebuildWhoseFlushFails_IsLogged_AndRebuildsOverNoHeldModelAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    var stream = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);
    await rig.DrainAsync(stream, 1, cancellationToken);
    rig.Locks.Refuses = true;
    var reads = _recordRowReads(rig);

    var result = await _rebuilderFor(rig, []).RebuildStreamsAsync(BEHIND, [stream], cancellationToken);

    var failure = await rig.Logs.FlushFailed.WaitAsync(cancellationToken);
    await Assert.That(failure!.Message).IsEqualTo("the apply lock was refused");
    await Assert.That(result.Success).IsTrue()
      .Because("the failed flush released its batches unwritten, so nothing held is left to write over the rebuild");
    await Assert.That(reads.Single().Held).IsEqualTo(0);
    await Assert.That(reads.Single().Written).IsEmpty();
    await Assert.That(rig.Coordinator.Reported).IsEmpty().Because("the released batches replay from their checkpoint");
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_ARebuildFlush_LeavesTheModelsOfAnotherPerspectiveHeldAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    var stream = Guid.NewGuid();
    await rig.StartAsync(cancellationToken);
    await rig.DrainAsync(stream, 1, cancellationToken);

    await rig.Worker.FlushWriteBehindForRebuildAsync("Tests.WriteBehind.Another", null, cancellationToken);

    await Assert.That(rig.Worker.WriteBehindModels).IsEqualTo(1);
    await Assert.That(rig.Sink.Calls).IsEqualTo(0);
  }

  [Test]
  [Timeout(30000)]
  public async Task WriteBehind_ARebuildFlush_OnAWorkerThatHoldsNothing_DoesNothingAsync(CancellationToken cancellationToken) {
    await using var rig = new WriteBehindRig();
    rig.Build();

    await rig.Worker.FlushWriteBehindForRebuildAsync(BEHIND, null, cancellationToken);

    await Assert.That(rig.Worker.WriteBehindModels).IsEqualTo(0);
    await Assert.That(rig.Sink.Calls).IsEqualTo(0);
  }

  /// <summary>What the rebuild saw as it read each row: the streams written by then, and how many models were held.</summary>
  private static ConcurrentQueue<(Guid StreamId, List<Guid> Written, int Held)> _recordRowReads(WriteBehindRig rig) {
    var reads = new ConcurrentQueue<(Guid StreamId, List<Guid> Written, int Held)>();
    rig.Runners.OnRowRead = streamId =>
      reads.Enqueue((streamId, [.. rig.Sink.Written.Select(w => w.StreamId)], rig.Worker.WriteBehindModels));
    return reads;
  }

  /// <summary>
  /// A rebuilder in a host whose worker is the rig's, with the rig's perspective; <paramref name="streams"/> are the
  /// streams the event store holds the perspective's events on, which a rebuild of every stream replays.
  /// </summary>
  private static PerspectiveRebuilder _rebuilderFor(WriteBehindRig rig, Guid[] streams) {
    foreach (var stream in rig.HeldStreams()) {
      rig.Runners.ClaimedEvents[stream] = rig.EventsOf(stream);
    }
    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(rig.Runners);
    services.AddSingleton(rig.Worker);
    services.AddSingleton<IEventStoreQuery>(new StreamsOnlyEventStoreQuery(streams));
    var provider = services.BuildServiceProvider();
    rig.Owns(provider);
    return new PerspectiveRebuilder(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PerspectiveRebuilder>.Instance);
  }

  /// <summary>An event store that holds one event of the perspective's type on each of its streams.</summary>
  private sealed class StreamsOnlyEventStoreQuery(Guid[] streams) : IEventStoreQuery {
    public IQueryable<EventStoreRecord> Query => streams.Select(stream => new EventStoreRecord {
      Id = Guid.NewGuid(),
      StreamId = stream,
      AggregateId = stream,
      AggregateType = "Test",
      Version = 1,
      EventType = TypeNameFormatter.Format(typeof(DrainModeTestEvent)),
      EventData = JsonElement.Parse("{}"),
      Metadata = new EnvelopeMetadata { MessageId = MessageId.New(), Hops = [] },
      CreatedAt = DateTime.UnixEpoch,
    }).AsQueryable();

    public IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId) => Query.Where(e => e.StreamId == streamId);
    public IQueryable<EventStoreRecord> GetEventsByType(string eventType) => Query.Where(e => e.EventType == eventType);
  }
}
