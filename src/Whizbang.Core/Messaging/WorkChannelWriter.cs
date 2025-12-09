using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Default implementation of IWorkChannelWriter using System.Threading.Channels.
/// Wraps an unbounded channel for async message processing.
/// Registered as singleton - shared between dispatcher/strategy and background worker.
/// </summary>
public class WorkChannelWriter : IWorkChannelWriter {
  private readonly Channel<OutboxWork> _channel;

  public WorkChannelWriter() {
    _channel = Channel.CreateUnbounded<OutboxWork>(new UnboundedChannelOptions {
      SingleReader = false,  // Multiple publisher loops may read concurrently
      SingleWriter = false,  // Multiple strategy instances may write concurrently
      AllowSynchronousContinuations = false  // Better performance isolation
    });
  }

  /// <summary>
  /// Gets the channel reader for consumers (background workers).
  /// </summary>
  public ChannelReader<OutboxWork> Reader => _channel.Reader;

  public ValueTask WriteAsync(OutboxWork work, CancellationToken ct = default) {
    return _channel.Writer.WriteAsync(work, ct);
  }

  public bool TryWrite(OutboxWork work) {
    return _channel.Writer.TryWrite(work);
  }

  /// <summary>
  /// Signals that no more work will be written to the channel.
  /// Consumers will complete after draining existing work.
  /// </summary>
  public void Complete() {
    _channel.Writer.Complete();
  }
}
