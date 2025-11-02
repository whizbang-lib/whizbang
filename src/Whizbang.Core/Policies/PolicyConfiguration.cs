namespace Whizbang.Core.Policies;

/// <summary>
/// Configuration for message processing determined by policy matching.
/// Contains routing, execution strategy, and resource configuration.
/// </summary>
public class PolicyConfiguration {
  /// <summary>
  /// Topic to route the message to
  /// </summary>
  public string? Topic { get; private set; }

  /// <summary>
  /// Stream key for ordering and partitioning
  /// </summary>
  public string? StreamKey { get; private set; }

  /// <summary>
  /// Type of execution strategy to use (e.g., SerialExecutor, ParallelExecutor)
  /// </summary>
  public Type? ExecutionStrategyType { get; private set; }

  /// <summary>
  /// Type of partition router to use (e.g., HashPartitionRouter)
  /// </summary>
  public Type? PartitionRouterType { get; private set; }

  /// <summary>
  /// Type of sequence provider to use (e.g., InMemorySequenceProvider)
  /// </summary>
  public Type? SequenceProviderType { get; private set; }

  /// <summary>
  /// Number of partitions for this stream
  /// </summary>
  public int? PartitionCount { get; private set; }

  /// <summary>
  /// Maximum concurrency for execution
  /// </summary>
  public int? MaxConcurrency { get; private set; }

  /// <summary>
  /// Sets the topic for message routing
  /// </summary>
  public PolicyConfiguration UseTopic(string topic) {
    Topic = topic;
    return this;
  }

  /// <summary>
  /// Sets the stream key for ordering and partitioning
  /// </summary>
  public PolicyConfiguration UseStreamKey(string streamKey) {
    StreamKey = streamKey;
    return this;
  }

  /// <summary>
  /// Sets the execution strategy type
  /// </summary>
  public PolicyConfiguration UseExecutionStrategy<TStrategy>() {
    ExecutionStrategyType = typeof(TStrategy);
    return this;
  }

  /// <summary>
  /// Sets the partition router type
  /// </summary>
  public PolicyConfiguration UsePartitionRouter<TRouter>() {
    PartitionRouterType = typeof(TRouter);
    return this;
  }

  /// <summary>
  /// Sets the sequence provider type
  /// </summary>
  public PolicyConfiguration UseSequenceProvider<TProvider>() {
    SequenceProviderType = typeof(TProvider);
    return this;
  }

  /// <summary>
  /// Sets the number of partitions
  /// </summary>
  public PolicyConfiguration WithPartitions(int count) {
    if (count <= 0) {
      throw new ArgumentOutOfRangeException(nameof(count), "Partition count must be greater than zero");
    }
    PartitionCount = count;
    return this;
  }

  /// <summary>
  /// Sets the maximum concurrency
  /// </summary>
  public PolicyConfiguration WithConcurrency(int maxConcurrency) {
    if (maxConcurrency <= 0) {
      throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "Max concurrency must be greater than zero");
    }
    MaxConcurrency = maxConcurrency;
    return this;
  }
}
