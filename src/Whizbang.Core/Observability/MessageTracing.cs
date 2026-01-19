using System.Reflection;
using System.Runtime.CompilerServices;

namespace Whizbang.Core.Observability;

/// <summary>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_Constructor_SetsAllPropertiesAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllPolicyDecisions_ReturnsEmpty_WhenNoHopsHaveTrailsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_RequiresAtLeastOneHopAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_AddHop_AddsHopToListAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_AddHop_MaintainsOrderedListAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentTopic_ReturnsNull_WhenNoHopsHaveTopicAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentTopic_ReturnsMostRecentNonNullTopicAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentStreamKey_ReturnsNull_WhenNoHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentStreamKey_ReturnsMostRecentNonNullStreamKeyAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentPartitionIndex_ReturnsNull_WhenNoHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentPartitionIndex_ReturnsMostRecentNonNullValueAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentSequenceNumber_ReturnsNull_WhenNoHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentSequenceNumber_ReturnsMostRecentNonNullValueAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentSecurityContext_ReturnsNull_WhenNoHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentSecurityContext_ReturnsMostRecentNonNullValueAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetMessageTimestamp_ReturnsFirstHopTimestampAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetMetadata_ReturnsNull_WhenKeyNotFoundAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetMetadata_ReturnsLatestValue_WhenKeyExistsInMultipleHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllMetadata_StitchesAllMetadataAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllMetadata_ReturnsEmpty_WhenNoHopsHaveMetadataAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllPolicyDecisions_ReturnsSingleHopDecisionsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllPolicyDecisions_StitchesDecisionsAcrossMultipleHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllPolicyDecisions_MaintainsChronologicalOrderAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllPolicyDecisions_SkipsHopsWithoutTrailsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageHop_Constructor_SetsAllPropertiesAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageHop_CallerInfo_CanBeNullAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageHop_SecurityContext_CanBeNullAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageHop_SecurityContext_CanBeSetAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageHop_Trail_CanBeNullAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageHop_Trail_CanBeSetAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageHop_Type_DefaultsToCurrentAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageHop_Type_CanBeSetToCausationAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageHop_CausationFields_AreNullForCurrentHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCausationHops_ReturnsEmpty_WhenNoCausationHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCausationHops_ReturnsOnlyCausationHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentHops_ReturnsOnlyCurrentHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentTopic_IgnoresCausationHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentStreamKey_IgnoresCausationHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentPartitionIndex_IgnoresCausationHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentSequenceNumber_IgnoresCausationHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetCurrentSecurityContext_IgnoresCausationHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetMetadata_IgnoresCausationHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllMetadata_IgnoresCausationHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageEnvelope_GetAllPolicyDecisions_IgnoresCausationHopsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:RecordHop_CapturesCallerMemberName_AutomaticallyAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:RecordHop_CapturesCallerFilePath_AutomaticallyAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:RecordHop_CapturesCallerLineNumber_AutomaticallyAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:RecordHop_SetsServiceName_ToEntryAssemblyAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:RecordHop_SetsMachineName_ToEnvironmentMachineNameAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:RecordHop_SetsTimestamp_ToApproximatelyNowAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:RecordHop_SetsTopicStreamAndStrategyAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:RecordHop_FromDifferentMethods_CapturesDifferentCallerInfoAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:RecordHop_WithPartitionAndSequence_SetsOptionalFieldsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:RecordHop_WithDuration_SetsDurationFieldAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageTrace_Constructor_InitializesWithMessageIdAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageTrace_Hops_IsInitializedEmptyAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageTrace_PolicyTrails_IsInitializedEmptyAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageTrace_AddHop_AddsToHopsListAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageTrace_AddPolicyTrail_AddsToTrailsListAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageTrace_SetOutcome_SetsSuccessAndErrorAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageTrace_RecordTiming_AddsToDictionaryAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageTrace_TotalDuration_CanBeSetAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/MessageTracingTests.cs:MessageTrace_WithCorrelationAndCausation_SetsPropertiesAsync</tests>
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
