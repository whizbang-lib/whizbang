using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Interface for writing inbox work to a processing channel.
/// Enables any caller of process_work_batch to route claimed inbox work
/// to the WorkCoordinatorPublisherWorker for processing.
/// Implementations are typically singleton and shared between WorkBatchCoordinator and publisher worker.
/// </summary>
/// <docs>messaging/inbox-channel</docs>
public interface IInboxChannelWriter {
  /// <summary>
  /// Gets the channel reader for consumers (WorkCoordinatorPublisherWorker).
  /// </summary>
  ChannelReader<InboxWork> Reader { get; }

  /// <summary>
  /// Asynchronously writes inbox work to the channel for processing.
  /// </summary>
  /// <param name="work">The inbox work to queue for processing</param>
  /// <param name="ct">Cancellation token</param>
  ValueTask WriteAsync(InboxWork work, CancellationToken ct = default);

  /// <summary>
  /// Attempts to write inbox work to the channel synchronously.
  /// </summary>
  /// <param name="work">The inbox work to queue for processing</param>
  /// <returns>True if the work was written; false if the channel is full or complete</returns>
  bool TryWrite(InboxWork work);

  /// <summary>
  /// Signals that no more work will be written to the channel.
  /// </summary>
  void Complete();

  /// <summary>
  /// Event raised when new inbox work has been written to the channel.
  /// The publisher worker subscribes to wake immediately.
  /// </summary>
  event Action? OnNewInboxWorkAvailable;

  /// <summary>
  /// Fires <see cref="OnNewInboxWorkAvailable"/> to wake the publisher worker.
  /// </summary>
  void SignalNewInboxWorkAvailable();
}
