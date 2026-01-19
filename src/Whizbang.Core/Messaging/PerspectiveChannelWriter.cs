using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Default implementation of IPerspectiveChannelWriter using System.Threading.Channels.
/// Creates an unbounded channel for perspective work distribution.
/// Thread-safe for concurrent writers and readers.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/PerspectiveChannelWriterTests.cs</tests>
public class PerspectiveChannelWriter : IPerspectiveChannelWriter {
  private readonly Channel<PerspectiveWork> _channel;

  /// <summary>
  /// Initializes a new instance of PerspectiveChannelWriter with an unbounded channel.
  /// Configured for multiple concurrent readers and writers.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/PerspectiveChannelWriterTests.cs:Constructor_CreatesUnboundedChannel_SuccessfullyAsync</tests>
  public PerspectiveChannelWriter() {
    _channel = Channel.CreateUnbounded<PerspectiveWork>(new UnboundedChannelOptions {
      SingleReader = false,  // Multiple perspective workers may read concurrently
      SingleWriter = false,  // WorkBatchCoordinator instances may write concurrently
      AllowSynchronousContinuations = false  // Better performance isolation
    });
  }

  /// <inheritdoc />
  public ChannelReader<PerspectiveWork> Reader => _channel.Reader;

  /// <inheritdoc />
  public ValueTask WriteAsync(PerspectiveWork work, CancellationToken ct = default) {
    return _channel.Writer.WriteAsync(work, ct);
  }

  /// <inheritdoc />
  public bool TryWrite(PerspectiveWork work) {
    return _channel.Writer.TryWrite(work);
  }

  /// <inheritdoc />
  public void Complete() {
    _channel.Writer.Complete();
  }
}
