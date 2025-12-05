using System.Reflection;
using System.Runtime.CompilerServices;

namespace Whizbang.Core.Observability;

/// <summary>
/// Static helper for recording message hops with automatic caller information capture.
/// Uses C# compiler magic attributes to capture call site information at zero runtime cost.
/// </summary>
public static class MessageTracing {
  /// <summary>
  /// Records a message hop with automatic caller information capture.
  /// The caller information is captured at compile time via attributes.
  /// </summary>
  /// <param name="serviceInstance">The service instance information for the service processing this message</param>
  /// <param name="topic">The topic being processed</param>
  /// <param name="streamKey">The stream key being processed</param>
  /// <param name="executionStrategy">The execution strategy being used</param>
  /// <param name="partitionIndex">Optional partition index</param>
  /// <param name="sequenceNumber">Optional sequence number</param>
  /// <param name="duration">Optional duration of processing</param>
  /// <param name="callerMemberName">Automatically captured calling method name</param>
  /// <param name="callerFilePath">Automatically captured calling file path</param>
  /// <param name="callerLineNumber">Automatically captured calling line number</param>
  /// <returns>A MessageHop with all information including caller details</returns>
  public static MessageHop RecordHop(
      ServiceInstanceInfo serviceInstance,
      string topic,
      string streamKey,
      string executionStrategy,
      int? partitionIndex = null,
      long? sequenceNumber = null,
      TimeSpan? duration = null,
      [CallerMemberName] string? callerMemberName = null,
      [CallerFilePath] string? callerFilePath = null,
      [CallerLineNumber] int? callerLineNumber = null
  ) {
    return new MessageHop {
      ServiceInstance = serviceInstance,
      Timestamp = DateTimeOffset.UtcNow,
      Topic = topic,
      StreamKey = streamKey,
      PartitionIndex = partitionIndex,
      SequenceNumber = sequenceNumber,
      ExecutionStrategy = executionStrategy,
      CallerMemberName = callerMemberName,
      CallerFilePath = callerFilePath,
      CallerLineNumber = callerLineNumber,
      Duration = duration ?? TimeSpan.Zero
    };
  }
}
