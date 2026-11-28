// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Whizbang.Testing.Async;

/// <summary>
/// A channel reader that remembers every <see cref="ChannelReader{T}.WaitToReadAsync"/> asked of it, so a test can say
/// exactly how many waits the channel underneath still holds.
/// </summary>
/// <remarks>
/// <para>
/// A bounded or unbounded channel queues each waiting reader and lets it go only when an item is written or the wait's
/// token is canceled. A wait that nobody awaits any more, on a token that is never canceled, stays queued for the
/// channel's life: a loop that asks for a fresh wait every pass and races it against a timeout grows by one waiter per
/// pass. Hand the code under test this reader in place of the channel's own, and <see cref="OutstandingWaits"/> counts
/// the waits that are neither completed nor canceled. <see cref="ReleaseCheck"/> holds that premise against the
/// runtime: a canceled wait is released, an abandoned one is kept, and one whose token source was only disposed is
/// kept.
/// </para>
/// <para>
/// Counting through the reader keeps every assertion exact and synchronous: no reflection, no heap reads, no timing.
/// <c>onWait</c>, when given, runs on the caller's thread once each wait has been asked of the inner reader, numbered
/// from one, so a test can move a fake clock or raise a signal at the moment a wait begins.
/// </para>
/// </remarks>
/// <typeparam name="T">The channel's item type.</typeparam>
/// <param name="inner">The channel's own reader.</param>
/// <param name="onWait">Runs after each wait is made, with its number.</param>
/// <docs>contributors/testing/loop-waits</docs>
/// <tests>tests/Whizbang.Testing.Tests/Async/LoopWaitProbeTests.cs</tests>
public sealed class CountingChannelReader<T>(ChannelReader<T> inner, Action<int>? onWait = null) : ChannelReader<T> {
  private readonly ChannelReader<T> _inner = inner ?? throw new ArgumentNullException(nameof(inner));
  private readonly Lock _lock = new();
  private readonly List<(Task<bool> Wait, CancellationToken Token)> _waits = [];

  /// <summary>How many waits were asked for in all.</summary>
  public int WaitCalls {
    get {
      lock (_lock) {
        return _waits.Count;
      }
    }
  }

  /// <summary>How many waits the channel still holds: neither completed nor canceled.</summary>
  public int OutstandingWaits {
    get {
      lock (_lock) {
        return _waits.Count(w => !w.Wait.IsCompleted && !w.Token.IsCancellationRequested);
      }
    }
  }

  /// <inheritdoc />
  public override Task Completion => _inner.Completion;

  /// <inheritdoc />
  public override bool CanCount => _inner.CanCount;

  /// <inheritdoc />
  public override int Count => _inner.Count;

  /// <inheritdoc />
  public override bool CanPeek => _inner.CanPeek;

  /// <inheritdoc />
  public override bool TryPeek([MaybeNullWhen(false)] out T item) => _inner.TryPeek(out item);

  /// <inheritdoc />
  public override bool TryRead([MaybeNullWhen(false)] out T item) => _inner.TryRead(out item);

  /// <inheritdoc />
  public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) {
    // Handed back as a task-backed value task over the inner wait, so a caller's AsTask() returns the very task
    // recorded here and its completion is observed directly.
    var wait = _inner.WaitToReadAsync(cancellationToken).AsTask();
    int number;
    lock (_lock) {
      _waits.Add((wait, cancellationToken));
      number = _waits.Count;
    }
    onWait?.Invoke(number);
    return new ValueTask<bool>(wait);
  }
}
