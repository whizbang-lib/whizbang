using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Interface for writing perspective work to a processing channel.
/// Enables the framework layer to queue perspective materialization work for background processing.
/// Implementations are typically singleton and shared between WorkBatchCoordinator and PerspectiveWorker.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/PerspectiveChannelWriterTests.cs</tests>
public interface IPerspectiveChannelWriter {
  /// <summary>
  /// Gets the channel reader for consumers (PerspectiveWorker).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/PerspectiveChannelWriterTests.cs:Constructor_CreatesUnboundedChannel_SuccessfullyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/PerspectiveChannelWriterTests.cs:Reader_SupportsMultipleConcurrentReaders_SuccessfullyAsync</tests>
  ChannelReader<PerspectiveWork> Reader { get; }

  /// <summary>
  /// Asynchronously writes perspective work to the channel for processing.
  /// Blocks if the channel is bounded and full.
  /// </summary>
  /// <param name="work">The perspective work to queue for processing</param>
  /// <param name="ct">Cancellation token</param>
  /// <returns>A task that completes when the work has been written to the channel</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/PerspectiveChannelWriterTests.cs:WriteAsync_WithValidWork_WritesToChannelAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/PerspectiveChannelWriterTests.cs:WriteAsync_SupportsMultipleConcurrentWriters_SuccessfullyAsync</tests>
  ValueTask WriteAsync(PerspectiveWork work, CancellationToken ct = default);

  /// <summary>
  /// Attempts to write perspective work to the channel synchronously.
  /// Returns false if the channel is bounded and full, or if the channel is complete.
  /// </summary>
  /// <param name="work">The perspective work to queue for processing</param>
  /// <returns>True if the work was written; false if the channel is full or complete</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/PerspectiveChannelWriterTests.cs:TryWrite_WithValidWork_ReturnsTrueAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/PerspectiveChannelWriterTests.cs:TryWrite_AfterComplete_ReturnsFalseAsync</tests>
  bool TryWrite(PerspectiveWork work);

  /// <summary>
  /// Signals that no more work will be written to the channel.
  /// Consumers will complete after draining existing work.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/PerspectiveChannelWriterTests.cs:Complete_MarksChannelAsComplete_SuccessfullyAsync</tests>
  void Complete();
}
