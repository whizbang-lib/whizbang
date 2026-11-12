// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Threading.Channels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Minting;
using Whizbang.Core.Serialization;
using Whizbang.Core.Tests.Workers;
using Whizbang.Core.ValueObjects;

// WHIZ150: the tests build their composites in a shared factory helper rather than inline in the helper's argument.
#pragma warning disable WHIZ150

namespace Whizbang.Core.Component.Tests.Minting;

/// <summary>An imported row, one event per stream: what a bulk import publishes.</summary>
/// <param name="StreamId">The row's own stream.</param>
/// <param name="Row">Its position in the import.</param>
public sealed record RowImported([property: StreamId] Guid StreamId, int Row) : IEvent;

/// <summary>A composite of imported rows, with a cap the tests choose.</summary>
public sealed class RowsImported : CompositeEventBase;

/// <summary>
/// Streaming events into composites (#2285): a batch is cut at whichever comes first of a message count (never above the
/// composite's cap), a byte size, or a debounced window capped by a maximum wait; order is kept, nothing read is dropped.
/// Every wait ends on a timer the test advances after it exists, or on an event the test writes.
/// </summary>
public class CompositeBatchesTests {
  private static readonly TimeSpan _ignoredDue = TimeSpan.FromHours(1);

  private static RowImported _row(int row) => new((Guid)TrackedGuid.New(), row);

  private static Func<IReadOnlyList<RowImported>, RowsImported> _factory(int cap = 10_000) =>
    rows => new RowsImported { Inner = [.. rows], MaxInnerEventsAllowed = cap };

  private static CompositeBatchOptions _options(int? messages = null, long? bytes = 0, int debounceMs = 100, int maxWaitMs = 1000) => new() {
    MaxBufferedMessages = messages,
    MaxBufferedBytes = bytes,
    DebounceInterval = TimeSpan.FromMilliseconds(debounceMs),
    MaxWait = TimeSpan.FromMilliseconds(maxWaitMs),
  };

  private static async IAsyncEnumerable<RowImported> _rows(params RowImported[] rows) {
    foreach (var row in rows) {
      await Task.Yield();
      yield return row;
    }
  }

  private static async Task<List<RowsImported>> _allAsync(IAsyncEnumerable<RowsImported> batches) {
    var all = new List<RowsImported>();
    await foreach (var batch in batches) {
      all.Add(batch);
    }
    return all;
  }

  private static int[] _rowsOf(RowsImported batch) => [.. batch.Inner.Cast<RowImported>().Select(r => r.Row)];

  [Test]
  public async Task ABatch_IsCutAtTheMessageCount_InSourceOrder_KeepingEachStreamAsync() {
    var rows = Enumerable.Range(1, 7).Select(_row).ToArray();

    var batches = await _allAsync(_rows(rows).ToCompositeBatches(_factory(), _options(messages: 3), new TimerAdvancingClock(_ignoredDue)));

    await Assert.That(batches.Select(b => _rowsOf(b).Length)).IsEquivalentTo([3, 3, 1], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(batches.SelectMany(_rowsOf)).IsEquivalentTo([1, 2, 3, 4, 5, 6, 7], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(batches.SelectMany(b => b.Inner.Cast<RowImported>().Select(r => r.StreamId)))
      .IsEquivalentTo(rows.Select(r => r.StreamId), TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task TheMessageCount_DefaultsToTheCompositesCap_AndIsNeverAboveItAsync() {
    var rows = Enumerable.Range(1, 5).Select(_row).ToArray();
    var probes = 0;
    RowsImported factory(IReadOnlyList<RowImported> batch) {
      if (batch.Count == 0) {
        probes++;
      }
      return _factory(cap: 2)(batch);
    }

    var byDefault = await _allAsync(_rows(rows).ToCompositeBatches(factory, _options(), new TimerAdvancingClock(_ignoredDue)));
    var aboveTheCap = await _allAsync(_rows(rows).ToCompositeBatches(factory, _options(messages: 50), new TimerAdvancingClock(_ignoredDue)));

    await Assert.That(byDefault.Select(b => b.Inner.Count)).IsEquivalentTo([2, 2, 1], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(aboveTheCap.Select(b => b.Inner.Count)).IsEquivalentTo([2, 2, 1], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(probes).IsEqualTo(2).Because("the cap is read once per run, from a composite over no events");
  }

  [Test]
  public async Task ABatch_IsCutAtTheByteSize_AndAnEventLargerThanTheLimitGoesAloneAsync() {
    var rows = Enumerable.Range(1, 6).Select(_row).ToArray();
    long[] sizes = [40, 40, 40, 500, 30, 30];

    var batches = await _allAsync(CompositeBatchExtensions.ToCompositeBatches(
      _rows(rows), _factory(), _options(bytes: 100), new TimerAdvancingClock(_ignoredDue), row => sizes[row.Row - 1]));

    await Assert.That(batches.Select(b => string.Join(',', _rowsOf(b)))).IsEquivalentTo(["1,2", "3", "4", "5,6"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task ABatchThatReachesTheByteLimitExactly_IsCutAtOnceAsync() {
    var rows = Enumerable.Range(1, 3).Select(_row).ToArray();

    var batches = await _allAsync(CompositeBatchExtensions.ToCompositeBatches(
      _rows(rows), _factory(), _options(bytes: 100), new TimerAdvancingClock(_ignoredDue), _ => 50));

    await Assert.That(batches.Select(b => _rowsOf(b).Length)).IsEquivalentTo([2, 1], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task WithNoByteLimit_SizesAreNeverMeasuredAsync() {
    var measured = 0;

    var batches = await _allAsync(CompositeBatchExtensions.ToCompositeBatches(
      _rows(_row(1), _row(2)), _factory(), _options(bytes: 0), new TimerAdvancingClock(_ignoredDue), _ => ++measured));

    await Assert.That(measured).IsEqualTo(0);
    await Assert.That(batches.Single().Inner.Count).IsEqualTo(2);
  }

  [Test]
  public async Task AnEmptySource_BuildsNoBatchAsync() {
    var batches = await _allAsync(_rows().ToCompositeBatches(_factory(), _options(), new TimerAdvancingClock(_ignoredDue)));

    await Assert.That(batches).IsEmpty();
  }

  [Test]
  [Timeout(60000)]
  public async Task AQuietSource_SendsTheBatch_OnceTheDebounceHasPassedAsync(CancellationToken cancellationToken) {
    var clock = new TimerAdvancingClock(_ignoredDue);
    var source = Channel.CreateUnbounded<RowImported>();
    await using var batches = source.Reader.ReadAllAsync(cancellationToken)
      .ToCompositeBatches(_factory(), _options(), clock).GetAsyncEnumerator(cancellationToken);
    var first = batches.MoveNextAsync().AsTask();

    await source.Writer.WriteAsync(_row(1), cancellationToken);
    var due = await clock.Created(1).WaitAsync(cancellationToken);
    clock.Advance(due);

    await Assert.That(await first.WaitAsync(cancellationToken)).IsTrue();
    await Assert.That(_rowsOf(batches.Current)).IsEquivalentTo([1]);
    await Assert.That(due).IsEqualTo(TimeSpan.FromMilliseconds(100));
    source.Writer.Complete();
  }

  [Test]
  [Timeout(60000)]
  public async Task AnEventWithinTheDebounce_JoinsTheBatch_AndRestartsTheWindowAsync(CancellationToken cancellationToken) {
    var clock = new TimerAdvancingClock(_ignoredDue);
    var source = Channel.CreateUnbounded<RowImported>();
    await using var batches = source.Reader.ReadAllAsync(cancellationToken)
      .ToCompositeBatches(_factory(), _options(), clock).GetAsyncEnumerator(cancellationToken);
    var first = batches.MoveNextAsync().AsTask();

    await source.Writer.WriteAsync(_row(1), cancellationToken);
    await clock.Created(1).WaitAsync(cancellationToken);
    clock.Advance(TimeSpan.FromMilliseconds(60));
    await source.Writer.WriteAsync(_row(2), cancellationToken);
    var restarted = await clock.Created(2).WaitAsync(cancellationToken);
    clock.Advance(restarted);

    await Assert.That(await first.WaitAsync(cancellationToken)).IsTrue();
    await Assert.That(_rowsOf(batches.Current)).IsEquivalentTo([1, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(restarted).IsEqualTo(TimeSpan.FromMilliseconds(100)).Because("the window restarts at the second event");
    source.Writer.Complete();
  }

  [Test]
  [Timeout(60000)]
  public async Task ABusySource_SendsTheBatch_NoLaterThanTheMaxWaitAfterItsFirstEventAsync(CancellationToken cancellationToken) {
    var clock = new TimerAdvancingClock(_ignoredDue);
    var source = Channel.CreateUnbounded<RowImported>();
    await using var batches = source.Reader.ReadAllAsync(cancellationToken)
      .ToCompositeBatches(_factory(), _options(debounceMs: 100, maxWaitMs: 250), clock).GetAsyncEnumerator(cancellationToken);
    var first = batches.MoveNextAsync().AsTask();

    await source.Writer.WriteAsync(_row(1), cancellationToken);
    await clock.Created(1).WaitAsync(cancellationToken);
    clock.Advance(TimeSpan.FromMilliseconds(90));
    await source.Writer.WriteAsync(_row(2), cancellationToken);
    await clock.Created(2).WaitAsync(cancellationToken);
    clock.Advance(TimeSpan.FromMilliseconds(90));
    await source.Writer.WriteAsync(_row(3), cancellationToken);
    var capped = await clock.Created(3).WaitAsync(cancellationToken);
    clock.Advance(capped);

    await Assert.That(capped).IsEqualTo(TimeSpan.FromMilliseconds(70)).Because("250 ms after the first event, not 100 ms after the last");
    await Assert.That(await first.WaitAsync(cancellationToken)).IsTrue();
    await Assert.That(_rowsOf(batches.Current)).IsEquivalentTo([1, 2, 3], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    source.Writer.Complete();
  }

  [Test]
  [Timeout(60000)]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AnEventArrivingAsTheWindowCloses_IsNeverLostAsync(bool eventFirst, CancellationToken cancellationToken) {
    var clock = new TimerAdvancingClock(_ignoredDue);
    var source = Channel.CreateUnbounded<RowImported>();
    var collected = new List<int>();
    var all = Task.Run(async () => {
      await foreach (var batch in source.Reader.ReadAllAsync(cancellationToken).ToCompositeBatches(_factory(), _options(), clock)
                       .WithCancellation(cancellationToken)) {
        collected.AddRange(_rowsOf(batch));
      }
    }, cancellationToken);

    await source.Writer.WriteAsync(_row(1), cancellationToken);
    var due = await clock.Created(1).WaitAsync(cancellationToken);
    // The window's timer and the next event complete together: whichever the race sees first, the event is kept.
    if (eventFirst) {
      source.Writer.TryWrite(_row(2));
      clock.Advance(due);
    } else {
      clock.Advance(due);
      source.Writer.TryWrite(_row(2));
    }
    source.Writer.Complete();
    await clock.AdvanceTimersUntilAsync(all, cancellationToken);

    await Assert.That(collected).IsEquivalentTo([1, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task WhenTheSourceThrows_WhatWasReadIsSent_ThenTheExceptionSurfacesAsync() {
    static async IAsyncEnumerable<RowImported> failing() {
      await Task.Yield();
      yield return _row(1);
      yield return _row(2);
      throw new InvalidOperationException("the import file is truncated");
    }
    var batches = new List<RowsImported>();

    var failure = await Assert.That(async () => {
      await foreach (var batch in failing().ToCompositeBatches(_factory(), _options(), new TimerAdvancingClock(_ignoredDue))) {
        batches.Add(batch);
      }
    }).Throws<InvalidOperationException>();

    await Assert.That(failure!.Message).IsEqualTo("the import file is truncated");
    await Assert.That(_rowsOf(batches.Single())).IsEquivalentTo([1, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  [Timeout(60000)]
  public async Task WhenCanceled_WhatWasReadIsSent_ThenTheCancellationSurfacesAsync(CancellationToken cancellationToken) {
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var clock = new TimerAdvancingClock(_ignoredDue);
    var source = Channel.CreateUnbounded<RowImported>();
    await using var batches = source.Reader.ReadAllAsync(cts.Token)
      .ToCompositeBatches(_factory(), _options(), clock).GetAsyncEnumerator(cts.Token);
    var first = batches.MoveNextAsync().AsTask();
    await source.Writer.WriteAsync(_row(1), cancellationToken);
    await clock.Created(1).WaitAsync(cancellationToken);

    await cts.CancelAsync();

    await Assert.That(await first.WaitAsync(cancellationToken)).IsTrue().Because("the event already read is sent");
    await Assert.That(_rowsOf(batches.Current)).IsEquivalentTo([1]);
    await Assert.That(async () => await batches.MoveNextAsync()).Throws<OperationCanceledException>();
  }

  [Test]
  public async Task TheOptions_AreValidatedAsync() {
    var source = _rows();
    await Assert.That(() => source.ToCompositeBatches(_factory(), _options(debounceMs: 0))).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => source.ToCompositeBatches(_factory(), _options(maxWaitMs: 0))).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => source.ToCompositeBatches(_factory(), null!)).Throws<ArgumentNullException>();
    await Assert.That(() => source.ToCompositeBatches<RowImported, RowsImported>(null!, _options())).Throws<ArgumentNullException>();
    await Assert.That(() => ((IAsyncEnumerable<RowImported>)null!).ToCompositeBatches(_factory(), _options())).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TheDefaultByteLimit_AndTheSystemClock_ApplyWhenNoneIsGivenAsync() {
    var options = new CompositeBatchOptions();

    var batches = await _allAsync(_rows(_row(1), _row(2)).ToCompositeBatches(_factory(), options));

    await Assert.That(options.MaxBufferedBytes).IsNull();
    await Assert.That(batches.Single().Inner.Count).IsEqualTo(2);
  }

  [Test]
  public async Task AnEventsSize_IsItsSerializedJson_AndAnUnregisteredTypeIsRefusedAsync() {
    var row = _row(7);
    var options = JsonContextRegistry.CreateCombinedOptions();
    var typeInfo = JsonContextRegistry.GetTypeInfoByName(TypeNameFormatter.AssemblyQualifiedNameOrDisplay(typeof(RowImported)), options)!;

    await Assert.That(CompositeBatchSize.Of(row, options)).IsEqualTo(JsonSerializer.SerializeToUtf8Bytes(row, typeInfo).Length);
    await Assert.That(() => CompositeBatchSize.Of(new object(), options)).Throws<InvalidOperationException>();
  }

  [Test]
  [Timeout(60000)]
  public async Task Publishing_AwaitsEachComposite_BeforeBuildingTheNext_AndCountsThemAsync(CancellationToken cancellationToken) {
    var dispatcher = new GatedPublisher();
    var rows = Enumerable.Range(1, 5).Select(_row).ToArray();

    var publishing = dispatcher.PublishCompositeBatchesAsync(_rows(rows), _factory(), _options(messages: 2),
      new TimerAdvancingClock(_ignoredDue), cancellationToken);
    await dispatcher.Started(1).WaitAsync(cancellationToken);

    await Assert.That(dispatcher.Published.Count).IsEqualTo(1).Because("the second composite waits for the first publish");
    dispatcher.ReleaseAll();
    var count = await publishing.WaitAsync(cancellationToken);

    await Assert.That(count).IsEqualTo(3);
    await Assert.That(dispatcher.Published.Select(c => c.Inner.Count)).IsEquivalentTo([2, 2, 1], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(() => ((IDispatcher)null!).PublishCompositeBatchesAsync(_rows(), _factory(), _options()))
      .Throws<ArgumentNullException>();
    var onTheSystemClock = await new GatedPublisher(released: true).PublishCompositeBatchesAsync(_rows(_row(1)), _factory(), _options(), cancellationToken);
    await Assert.That(onTheSystemClock).IsEqualTo(1);
  }

  /// <summary>A dispatcher whose publishes wait until the test releases them.</summary>
  private sealed class GatedPublisher : FakeDispatcher, IDispatcher {
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GatedPublisher(bool released = false) {
      if (released) {
        _released.TrySetResult();
      }
    }
    private readonly List<TaskCompletionSource> _started = [];
    private readonly Lock _gate = new();

    public List<RowsImported> Published { get; } = [];

    public Task Started(int count) {
      lock (_gate) {
        return _signal(count).Task;
      }
    }

    public void ReleaseAll() => _released.TrySetResult();

    public new async Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options) {
      TaskCompletionSource started;
      lock (_gate) {
        Published.Add((RowsImported)(object)eventData!);
        started = _signal(Published.Count);
      }
      started.TrySetResult();
      await _released.Task;
      return null!;
    }

    private TaskCompletionSource _signal(int count) {
      while (_started.Count < count) {
        _started.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
      }
      return _started[count - 1];
    }
  }
}
