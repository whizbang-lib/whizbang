using System.Threading.Channels;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for three <see cref="SlidingWindowBatcher{T}"/> branches the primary suite doesn't
/// reach: a phantom "ready" signal that turns out to have nothing to read, a zero-length sliding
/// window that must flush the instant anything is buffered, and the reader's own wait completing
/// canceled mid-accumulation. This batcher backs every debounced-batch coalescing pipeline in the
/// library — a race handled wrong here either yields a hollow empty batch downstream code doesn't
/// expect, or turns an ordinary reader-side cancellation into an unhandled fault instead of a clean
/// stop.
/// </summary>
public class SlidingWindowBatcherCoverageTests {

  /// <summary>Claims an item is ready on the first wait even though none is actually buffered —
  /// the exact "WaitToReadAsync said true but TryRead returned nothing" race the batcher's retry
  /// exists to survive, reproduced deterministically instead of via real concurrency.</summary>
  private sealed class _phantomReadyReader : ChannelReader<int> {
    private int _waitCalls;
    private readonly Queue<int> _queue = new();

    public override bool TryRead(out int item) {
      if (_queue.Count > 0) {
        item = _queue.Dequeue();
        return true;
      }
      item = default;
      return false;
    }

    public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) {
      _waitCalls++;
      if (_waitCalls == 1) {
        return ValueTask.FromResult(true);
      }
      return ValueTask.FromResult(_queue.Count > 0);
    }
  }

  /// <summary>What breaks: a phantom "ready" signal must retry, never yield an empty batch —
  /// downstream flush logic assumes every yielded batch has at least one item.</summary>
  [Test]
  public async Task ReadBatchesAsync_PhantomReadySignal_NeverYieldsAnEmptyBatchAsync() {
    var reader = new _phantomReadyReader();
    var batcher = new SlidingWindowBatcher<int>(reader, new SlidingWindowBatcherOptions());

    var batches = new List<IReadOnlyList<int>>();
    await foreach (var batch in batcher.ReadBatchesAsync(CancellationToken.None)) {
      batches.Add(batch);
    }

    await Assert.That(batches).IsEmpty()
      .Because("the phantom signal never actually had anything buffered — the retry must loop back to a real wait, not fabricate an empty batch for the caller to choke on");
  }

  /// <summary>What breaks: a zero-length sliding window means "no debounce grace period" — the
  /// batcher must flush the moment anything is buffered instead of waiting out MaxWait, or a
  /// consumer that asked for immediate delivery would see the same latency as one that didn't.</summary>
  [Test]
  public async Task ReadBatchesAsync_ZeroSlidingWindow_FlushesWithoutWaitingOutMaxWaitAsync() {
    var channel = Channel.CreateUnbounded<int>();
    await channel.Writer.WriteAsync(42);
    var options = new SlidingWindowBatcherOptions {
      MaxSize = 10,
      SlidingWindow = TimeSpan.Zero,
      MaxWait = TimeSpan.FromSeconds(5),
    };
    var batcher = new SlidingWindowBatcher<int>(channel.Reader, options);

    var batches = new List<IReadOnlyList<int>>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await foreach (var batch in batcher.ReadBatchesAsync(cts.Token)) {
      batches.Add(batch);
      channel.Writer.Complete();
    }

    await Assert.That(batches.Count).IsEqualTo(1)
      .Because("a zero-length window must not silently degrade into waiting out the full MaxWait cap");
    await Assert.That(batches[0]).IsEquivalentTo([42]);
  }

  /// <summary>Completes the second wait canceled — independent of the caller's own token — to
  /// simulate the reader's own wait observing cancellation mid-accumulation.</summary>
  private sealed class _cancelingSecondWaitReader : ChannelReader<int> {
    private int _waitCalls;
    private readonly Queue<int> _queue = new();

    public void Seed(int item) => _queue.Enqueue(item);

    public override bool TryRead(out int item) {
      if (_queue.Count > 0) {
        item = _queue.Dequeue();
        return true;
      }
      item = default;
      return false;
    }

    public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) {
      _waitCalls++;
      if (_waitCalls == 2) {
        return new ValueTask<bool>(Task.FromCanceled<bool>(new CancellationToken(canceled: true)));
      }
      return ValueTask.FromResult(true);
    }
  }

  /// <summary>What breaks: if a canceled reader-side wait escaped mid-accumulation instead of
  /// ending the enumerable cleanly, every consumer of this batcher would need its own
  /// try/catch around a supposedly ordinary stop — turning one shared coalescing primitive's
  /// shutdown contract into something each caller has to re-verify.</summary>
  [Test]
  public async Task ReadBatchesAsync_ReaderWaitCanceledMidAccumulation_StopsCleanlyAsync() {
    var reader = new _cancelingSecondWaitReader();
    reader.Seed(1);
    var options = new SlidingWindowBatcherOptions {
      MaxSize = 10,
      SlidingWindow = TimeSpan.FromSeconds(1),
      MaxWait = TimeSpan.FromSeconds(2),
    };
    var batcher = new SlidingWindowBatcher<int>(reader, options);

    var batches = new List<IReadOnlyList<int>>();
    await foreach (var batch in batcher.ReadBatchesAsync(CancellationToken.None)) {
      batches.Add(batch);
    }

    await Assert.That(batches).IsEmpty()
      .Because("the accumulating batch is discarded, not yielded, on a canceled reader wait — and no exception escapes past the enumerable's clean stop");
  }
}
