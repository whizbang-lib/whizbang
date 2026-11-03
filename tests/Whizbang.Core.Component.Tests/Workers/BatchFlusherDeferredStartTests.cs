// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A flush worker's flusher starts when the worker starts (#1636), not when it is constructed: the host starts the five
/// flush workers at Accept commands, so nothing they flush reaches the database before the instance's row records that
/// it is eligible for work, and none of them holds a schema-ready wait of its own. Items enqueued before then wait in
/// the channel; a flusher stopped before it ever started flushes nothing.
/// </summary>
/// <remarks>
/// Every assertion waits on a signal the flusher gives: its flush callback, or <see cref="BatchFlusher{T}.DisposeAsync"/>
/// returning, which for a started flusher awaits the drain.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/BatchFlusher.cs</code-under-test>
[Category("Workers")]
public class BatchFlusherDeferredStartTests {
  private static readonly BatchFlusherOptions _immediate = new() { CoalesceWindowMs = 0, MaxBatchSize = 10, ImmediateFlushThreshold = 1 };

  [Test]
  [Timeout(30000)]
  public async Task AnUnstartedFlusher_FlushesNothing_AndStoppingItDropsWhatWasQueuedAsync(CancellationToken cancellationToken) {
    var flushed = new List<int>();
    var flusher = new BatchFlusher<int>((items, _) => {
      flushed.AddRange(items);
      return Task.CompletedTask;
    }, _immediate, NullLogger.Instance, timeProvider: null, started: false);

    await flusher.EnqueueAsync(7, cancellationToken);
    await flusher.DisposeAsync();

    await Assert.That(flushed).IsEmpty()
      .Because("a flusher whose worker never started never touches the database, even while it is stopped");
    await Assert.That(flusher.StoppedSignal.IsCompleted).IsTrue();
  }

  [Test]
  [Timeout(30000)]
  public async Task Starting_FlushesWhatWasQueuedBeforeItAsync(CancellationToken cancellationToken) {
    var batch = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var flusher = new BatchFlusher<int>((items, _) => {
      batch.TrySetResult(items);
      return Task.CompletedTask;
    }, _immediate, NullLogger.Instance, timeProvider: null, started: false);
    await flusher.EnqueueAsync(7, cancellationToken);

    flusher.Start();
    flusher.Start();

    await Assert.That(await batch.Task.WaitAsync(cancellationToken)).IsEquivalentTo([7])
      .Because("an item enqueued before the worker started waits in the channel and is flushed once it starts");
  }

  [Test]
  [Timeout(30000)]
  public async Task AFlusherStartedAtConstruction_StartingAgainChangesNothingAsync(CancellationToken cancellationToken) {
    var batch = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var flusher = new BatchFlusher<int>((items, _) => {
      batch.TrySetResult(items);
      return Task.CompletedTask;
    }, _immediate, NullLogger.Instance);

    flusher.Start();
    await flusher.EnqueueAsync(3, cancellationToken);

    await Assert.That(await batch.Task.WaitAsync(cancellationToken)).IsEquivalentTo([3]);
  }
}
