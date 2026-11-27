// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Whizbang.Core.Messaging;
using Whizbang.Testing.Async;

namespace Whizbang.Core.Tests.Workers;

/// <summary>A drain channel read through a <see cref="CountingChannelReader{T}"/> over a real unbounded channel.</summary>
internal sealed class CountingDrainChannel : IPerspectiveDrainChannel {
  private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

  public CountingDrainChannel() => Counting = new CountingChannelReader<Guid>(_channel.Reader);

  /// <summary>The reader the worker is handed, which counts the waits the channel still holds.</summary>
  public CountingChannelReader<Guid> Counting { get; }
  public ChannelReader<Guid> Reader => Counting;
  public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) =>
    _channel.Writer.WriteAsync(streamId, cancellationToken);
  public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
}

/// <summary>A perspective work channel read through a <see cref="CountingChannelReader{T}"/> over a real unbounded channel.</summary>
internal sealed class CountingWorkChannel : IPerspectiveChannelWriter {
  private readonly Channel<PerspectiveWork> _channel = Channel.CreateUnbounded<PerspectiveWork>();

  public CountingWorkChannel() => Counting = new CountingChannelReader<PerspectiveWork>(_channel.Reader);

  /// <summary>The reader the worker is handed, which counts the waits the channel still holds.</summary>
  public CountingChannelReader<PerspectiveWork> Counting { get; }
  public ChannelReader<PerspectiveWork> Reader => Counting;
  public ValueTask WriteAsync(PerspectiveWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
  public bool TryWrite(PerspectiveWork work) => _channel.Writer.TryWrite(work);
  public void Complete() => _channel.Writer.TryComplete();
}
