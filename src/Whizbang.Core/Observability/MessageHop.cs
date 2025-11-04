using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// The type of hop - whether it represents processing of the current message
/// or carry-forward information from the causation/parent message.
/// </summary>
public enum HopType {
  /// <summary>
  /// This hop represents processing of the current message.
  /// </summary>
  Current = 0,

  /// <summary>
  /// This hop is carried forward from the causation/parent message.
  /// Used for distributed tracing to show what led to the current message.
  /// </summary>
  Causation = 1
}

/// <summary>
/// Represents a single hop in a message's journey through the system.
/// Records where and when the message was processed, including caller information for debugging.
/// Can represent either a hop for the current message or carry-forward hop from the causation message.
/// </summary>
public record MessageHop {
  /// <summary>
  /// The type of hop - Current (for this message) or Causation (from parent message).
  /// Defaults to Current. Causation hops are carried forward for distributed tracing.
  /// </summary>
  public HopType Type { get; init; } = HopType.Current;

  /// <summary>
  /// The MessageId of the causation/parent message (only for Causation hops).
  /// Null for Current hops (the current message's ID is on the envelope).
  /// </summary>
  public MessageId? CausationId { get; init; }

  /// <summary>
  /// The MessageId of the causation/parent message (only for Causation hops).
  /// Null for Current hops (the current message's ID is on the envelope).
  /// </summary>
  public CorrelationId? CorrelationId { get; init; }

  /// <summary>
  /// The type name of the causation/parent message (only for Causation hops).
  /// Useful for debugging to understand what type of message led to this one.
  /// Null for Current hops.
  /// </summary>
  public string? CausationType { get; init; }

  /// <summary>
  /// The service that processed this message.
  /// Typically the entry assembly name.
  /// </summary>
  public required string ServiceName { get; init; }

  /// <summary>
  /// The machine that processed this message.
  /// Useful for distributed tracing across nodes.
  /// </summary>
  public string MachineName { get; init; } = Environment.MachineName;

  /// <summary>
  /// When this hop occurred.
  /// </summary>
  public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

  /// <summary>
  /// The topic at this hop.
  /// </summary>
  public string Topic { get; init; } = string.Empty;

  /// <summary>
  /// The stream key at this hop.
  /// </summary>
  public string StreamKey { get; init; } = string.Empty;

  /// <summary>
  /// The partition index at this hop (if applicable).
  /// </summary>
  public int? PartitionIndex { get; init; }

  /// <summary>
  /// The sequence number at this hop (if applicable).
  /// </summary>
  public long? SequenceNumber { get; init; }

  /// <summary>
  /// The execution strategy used at this hop (e.g., "SerialExecutor", "ParallelExecutor").
  /// </summary>
  public string ExecutionStrategy { get; init; } = string.Empty;

  /// <summary>
  /// The security context at this hop (user, tenant, etc.).
  /// Can change from hop to hop as messages cross service boundaries.
  /// If null, inherits from previous hop.
  /// </summary>
  public SecurityContext? SecurityContext { get; init; }

  /// <summary>
  /// Metadata for this hop (tags, flags, custom data).
  /// Later hops override earlier hops for same keys when stitched together.
  /// If null, inherits from previous hop.
  /// </summary>
  public IReadOnlyDictionary<string, object>? Metadata { get; init; }

  /// <summary>
  /// Policy decisions made at this hop.
  /// Records all policy evaluations that occurred during processing at this point.
  /// If null, no policies were evaluated at this hop.
  /// </summary>
  public PolicyDecisionTrail? Trail { get; init; }

  /// <summary>
  /// The name of the calling method.
  /// Automatically captured via [CallerMemberName] attribute.
  /// Enables "jump to line" functionality in VSCode extension.
  /// </summary>
  public string? CallerMemberName { get; init; }

  /// <summary>
  /// The file path of the calling code.
  /// Automatically captured via [CallerFilePath] attribute.
  /// Enables "jump to line" functionality in VSCode extension.
  /// </summary>
  public string? CallerFilePath { get; init; }

  /// <summary>
  /// The line number of the calling code.
  /// Automatically captured via [CallerLineNumber] attribute.
  /// Enables "jump to line" functionality in VSCode extension.
  /// </summary>
  public int? CallerLineNumber { get; init; }

  /// <summary>
  /// How long this hop took to process.
  /// </summary>
  public TimeSpan Duration { get; init; }
}
