// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A channel reader that remembers every <see cref="ChannelReader{T}.WaitToReadAsync"/> asked of it, so a test can
/// say how many waits the channel underneath still holds (#2613).
/// </summary>
/// <remarks>
/// <para>
/// A wait is held until it completes or its token is canceled. An unbounded or bounded channel queues each waiting
/// reader and lets it go only when an item is written or the reader's token is canceled; a wait nobody awaits any more
/// and whose token is never canceled stays queued for the channel's life. <see cref="ChannelWaitReleaseTests"/> holds
/// that premise against the runtime with the garbage collector, including that disposing the token's source is not
/// enough. Counting through the reader the code under test is handed, rather than reading the channel's private
/// queue or the heap, keeps every assertion exact and synchronous: no reflection, no collection, no timing.
/// </para>
/// <para>
/// Each wait is handed back as a task-backed value task over the inner wait, so the caller's <c>AsTask()</c> returns
/// the very task recorded here and its completion is observed directly. <c>onWait</c>, when given, runs on the
/// caller's thread once each wait has been asked of the inner reader, numbered from one, so a test can move a fake
/// clock at the moment a wait begins.
/// </para>
/// </remarks>
internal sealed class WaitTrackingReader<T>(ChannelReader<T> inner, Action<int>? onWait = null) : ChannelReader<T> {
  private readonly Lock _lock = new();
  private readonly List<(Task<bool> Wait, CancellationToken Token)> _waits = [];

  /// <summary>How many waits were asked for.</summary>
  public int WaitCalls {
    get {
      lock (_lock) {
        return _waits.Count;
      }
    }
  }

  /// <summary>How many waits the channel still holds: neither completed nor canceled.</summary>
  public int HeldWaits {
    get {
      lock (_lock) {
        return _waits.Count(w => !w.Wait.IsCompleted && !w.Token.IsCancellationRequested);
      }
    }
  }

  /// <inheritdoc />
  public override bool TryRead(out T item) => inner.TryRead(out item!);

  /// <inheritdoc />
  public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) {
    var wait = inner.WaitToReadAsync(cancellationToken).AsTask();
    int number;
    lock (_lock) {
      _waits.Add((wait, cancellationToken));
      number = _waits.Count;
    }
    onWait?.Invoke(number);
    return new ValueTask<bool>(wait);
  }
}

/// <summary>A drain channel whose reader is a <see cref="WaitTrackingReader{T}"/> over a real unbounded channel.</summary>
internal sealed class WaitTrackingDrainChannel : Whizbang.Core.Messaging.IPerspectiveDrainChannel {
  private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

  public WaitTrackingDrainChannel() => TrackingReader = new WaitTrackingReader<Guid>(_channel.Reader);

  public WaitTrackingReader<Guid> TrackingReader { get; }
  public ChannelReader<Guid> Reader => TrackingReader;
  public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) =>
    _channel.Writer.WriteAsync(streamId, cancellationToken);
  public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
}

/// <summary>A perspective work channel whose reader is a <see cref="WaitTrackingReader{T}"/> over a real unbounded channel.</summary>
internal sealed class WaitTrackingWorkChannel : Whizbang.Core.Messaging.IPerspectiveChannelWriter {
  private readonly Channel<Whizbang.Core.Messaging.PerspectiveWork> _channel =
    Channel.CreateUnbounded<Whizbang.Core.Messaging.PerspectiveWork>();

  public WaitTrackingWorkChannel() =>
    TrackingReader = new WaitTrackingReader<Whizbang.Core.Messaging.PerspectiveWork>(_channel.Reader);

  public WaitTrackingReader<Whizbang.Core.Messaging.PerspectiveWork> TrackingReader { get; }
  public ChannelReader<Whizbang.Core.Messaging.PerspectiveWork> Reader => TrackingReader;
  public ValueTask WriteAsync(Whizbang.Core.Messaging.PerspectiveWork work, CancellationToken ct = default) =>
    _channel.Writer.WriteAsync(work, ct);
  public bool TryWrite(Whizbang.Core.Messaging.PerspectiveWork work) => _channel.Writer.TryWrite(work);
  public void Complete() => _channel.Writer.TryComplete();
}
