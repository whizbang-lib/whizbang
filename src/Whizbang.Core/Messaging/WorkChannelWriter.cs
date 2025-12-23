using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Default implementation of IWorkChannelWriter using System.Threading.Channels.
/// Wraps an unbounded channel for async message processing.
/// Registered as singleton - shared between dispatcher/strategy and background worker.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:TestWorkChannelWriter</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerChannelTests.cs:TestWorkChannelWriter</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerStartupTests.cs:TestWorkChannelWriter</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:TestWorkChannelWriter</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:TestWorkChannelWriter</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyImmediateProcessingTests.cs:TestWorkChannelWriter</tests>
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
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:TestWorkChannelWriter.Reader</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerChannelTests.cs:TestWorkChannelWriter.Reader</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerStartupTests.cs:TestWorkChannelWriter.Reader</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:TestWorkChannelWriter.Reader</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:TestWorkChannelWriter.Reader</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyImmediateProcessingTests.cs:TestWorkChannelWriter.Reader</tests>
  public ChannelReader<OutboxWork> Reader => _channel.Reader;

  /// <summary>
  /// Asynchronously writes work to the channel, waiting if necessary.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:TestWorkChannelWriter.WriteAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerChannelTests.cs:TestWorkChannelWriter.WriteAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerStartupTests.cs:TestWorkChannelWriter.WriteAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:TestWorkChannelWriter.WriteAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:TestWorkChannelWriter.WriteAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyImmediateProcessingTests.cs:TestWorkChannelWriter.WriteAsync</tests>
  public ValueTask WriteAsync(OutboxWork work, CancellationToken ct = default) {
    return _channel.Writer.WriteAsync(work, ct);
  }

  /// <summary>
  /// Attempts to write work to the channel immediately without waiting.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:TestWorkChannelWriter.TryWrite</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerChannelTests.cs:TestWorkChannelWriter.TryWrite</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerStartupTests.cs:TestWorkChannelWriter.TryWrite</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:TestWorkChannelWriter.TryWrite</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:TestWorkChannelWriter.TryWrite</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyImmediateProcessingTests.cs:TestWorkChannelWriter.TryWrite</tests>
  public bool TryWrite(OutboxWork work) {
    return _channel.Writer.TryWrite(work);
  }

  /// <summary>
  /// Signals that no more work will be written to the channel.
  /// Consumers will complete after draining existing work.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerDatabaseReadinessTests.cs:TestWorkChannelWriter.Complete</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerChannelTests.cs:TestWorkChannelWriter.Complete</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerStartupTests.cs:TestWorkChannelWriter.Complete</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerRaceConditionTests.cs:TestWorkChannelWriter.Complete</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/WorkCoordinatorPublisherWorkerMetricsTests.cs:TestWorkChannelWriter.Complete</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ScopedWorkCoordinatorStrategyImmediateProcessingTests.cs:TestWorkChannelWriter.Complete</tests>
  public void Complete() {
    _channel.Writer.Complete();
  }
}
