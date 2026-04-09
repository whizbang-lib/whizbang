using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Default implementation of IInboxChannelWriter using System.Threading.Channels.
/// Creates an unbounded channel for inbox work distribution.
/// Thread-safe for concurrent writers and readers.
/// </summary>
/// <docs>messaging/inbox-channel</docs>
public class InboxChannelWriter : IInboxChannelWriter {
  private readonly Channel<InboxWork> _channel;

  /// <summary>
  /// Initializes a new instance with an unbounded channel.
  /// </summary>
  public InboxChannelWriter() {
    _channel = Channel.CreateUnbounded<InboxWork>(new UnboundedChannelOptions {
      SingleReader = false,
      SingleWriter = false,
      AllowSynchronousContinuations = false
    });
  }

  /// <inheritdoc />
  public ChannelReader<InboxWork> Reader => _channel.Reader;

  /// <inheritdoc />
  public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) {
    var result = _channel.Writer.WriteAsync(work, ct);
    SignalNewInboxWorkAvailable();
    return result;
  }

  /// <inheritdoc />
  public bool TryWrite(InboxWork work) {
    var written = _channel.Writer.TryWrite(work);
    if (written) {
      SignalNewInboxWorkAvailable();
    }
    return written;
  }

  /// <inheritdoc />
  public void Complete() {
    _channel.Writer.Complete();
  }

  /// <inheritdoc />
  public event Action? OnNewInboxWorkAvailable;

  /// <inheritdoc />
  public void SignalNewInboxWorkAvailable() {
    OnNewInboxWorkAvailable?.Invoke();
  }
}
