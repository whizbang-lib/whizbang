// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Component.Tests.Minting;
using Whizbang.Core.Minting;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Async;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A batching window that an arrival ends releases the deadline it raced against the arrival (loop-waits audit).
/// </summary>
/// <remarks>
/// <para>
/// Each window races "the next item arrives" against "the window's deadline passes". When the item wins, the deadline
/// is a delay on the injected clock that is still pending. These windows cancel it once the race is decided, so it is
/// released at once; a window that only disposed its token source, or forgot it, would leave one pending delay per
/// arrival until each fired. These tests hold the cancel in place: many arrivals, each inside the open window, on a
/// clock that never moves, and the clock must still hold only the window that is open now.
/// </para>
/// <para>
/// The deadlines are counted through the clock the code is handed (<see cref="CountingTimeProvider"/>), never by timing.
/// The positive control is the open window itself: one live deadline while the loop waits, so the counter sees them.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/PerStreamSerializer.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Minting/CompositeBatches.cs</code-under-test>
public class RaceLoserReleaseTests {
  private const int ARRIVALS = 20;
  private static readonly TimeSpan _window = TimeSpan.FromSeconds(1);

  private sealed record StreamItem(Guid StreamId, int Number);

  [Test]
  [Timeout(30000)]
  public async Task PerStreamSerializerDrainWindow_EndedByArrivals_HoldsOnlyTheOpenWindowsDeadlineAsync(CancellationToken cancellationToken) {
    var fake = new FakeTimeProvider();
    var clock = new CountingTimeProvider(fake);
    var streamId = (Guid)TrackedGuid.New();
    var processed = 0;
    var allProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var serializer = new PerStreamSerializer<StreamItem>(
      streamIdSelector: item => item.StreamId,
      processor: (_, _) => {
        if (Interlocked.Increment(ref processed) == ARRIVALS + 1) {
          allProcessed.TrySetResult();
        }
        return Task.CompletedTask;
      },
      logger: NullLogger.Instance,
      options: new PerStreamSerializerOptions {
        DrainBatchWindow = _window,
        StreamChannelCapacity = ARRIVALS * 4,
        IdleSweepInterval = TimeSpan.FromHours(1),
      },
      timeProvider: clock);
    var baseline = clock.CreatedTimers; // the idle sweep's timer

    await serializer.EnqueueAsync(new StreamItem(streamId, 0), cancellationToken);
    var maxLive = 0;
    for (var arrival = 1; arrival <= ARRIVALS; arrival++) {
      // The window armed its deadline for this wait: the loop is parked in the race.
      await clock.Created(baseline + arrival).WaitAsync(cancellationToken);
      maxLive = Math.Max(maxLive, clock.LiveTimers - baseline);
      await serializer.EnqueueAsync(new StreamItem(streamId, arrival), cancellationToken);
    }
    await clock.Created(baseline + ARRIVALS + 1).WaitAsync(cancellationToken);

    await Assert.That(maxLive).IsEqualTo(1)
      .Because("the open window holds one deadline, and the counter must see it");
    await Assert.That(clock.LiveTimers - baseline).IsEqualTo(1)
      .Because("each arrival that won the race must cancel the deadline it raced, not leave it pending until it fires");

    fake.Advance(_window);
    await allProcessed.Task.WaitAsync(cancellationToken);
    await Assert.That(clock.LiveTimers - baseline).IsEqualTo(0)
      .Because("the last window closed on its deadline, which fired and released its timer");
  }

  [Test]
  [Timeout(30000)]
  public async Task CompositeBatchWindow_EndedByArrivals_HoldsOnlyTheOpenWindowsDeadlineAsync(CancellationToken cancellationToken) {
    var fake = new FakeTimeProvider();
    var clock = new CountingTimeProvider(fake);
    var source = Channel.CreateUnbounded<RowImported>();
    var options = new CompositeBatchOptions {
      MaxBufferedBytes = 0,
      DebounceInterval = _window,
      MaxWait = TimeSpan.FromHours(1),
    };
    await using var batches = source.Reader.ReadAllAsync(cancellationToken)
      .ToCompositeBatches(_composite, options, clock)
      .GetAsyncEnumerator(cancellationToken);

    // The first batch is cut only when a window closes, so this stays pending while every arrival joins it.
    var first = batches.MoveNextAsync();
    var maxLive = 0;
    source.Writer.TryWrite(_row(0));
    for (var arrival = 1; arrival <= ARRIVALS; arrival++) {
      await clock.Created(arrival).WaitAsync(cancellationToken);
      maxLive = Math.Max(maxLive, clock.LiveTimers);
      source.Writer.TryWrite(_row(arrival));
    }
    await clock.Created(ARRIVALS + 1).WaitAsync(cancellationToken);

    await Assert.That(maxLive).IsEqualTo(1)
      .Because("the open window holds one deadline, and the counter must see it");
    await Assert.That(clock.LiveTimers).IsEqualTo(1)
      .Because("each arrival that won the race must cancel the deadline it raced, not leave it pending until it fires");

    fake.Advance(_window);
    await Assert.That(await first.AsTask().WaitAsync(cancellationToken)).IsTrue();
    await Assert.That(batches.Current.Inner.Count).IsEqualTo(ARRIVALS + 1);
    source.Writer.Complete();
  }

  private static RowImported _row(int row) => new((Guid)TrackedGuid.New(), row);

  private static RowsImported _composite(IReadOnlyList<RowImported> rows) =>
    new() { Inner = [.. rows], MaxInnerEventsAllowed = 10_000 };
}
