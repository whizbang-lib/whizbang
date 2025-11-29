using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// Tracks the complete journey of a message through the system.
/// Used for time-travel debugging, performance analysis, and policy visualization.
/// </summary>
/// <remarks>
/// Creates a new message trace.
/// </remarks>
/// <param name="messageId">The message ID being traced</param>
public class MessageTrace(MessageId messageId) {
  /// <summary>
  /// The unique identifier of the message being traced.
  /// </summary>
  public MessageId MessageId { get; } = messageId;

  /// <summary>
  /// The correlation ID for workflow grouping.
  /// </summary>
  public CorrelationId? CorrelationId { get; init; }

  /// <summary>
  /// The causation ID for parent-child relationships.
  /// </summary>
  public MessageId? CausationId { get; init; }

  /// <summary>
  /// All hops this message took through the system.
  /// Each hop records where the message was processed with caller information.
  /// </summary>
  public List<MessageHop> Hops { get; } = [];

  /// <summary>
  /// All policy decision trails from each stage of processing.
  /// </summary>
  public List<PolicyDecisionTrail> PolicyTrails { get; } = [];

  /// <summary>
  /// Whether the message processing succeeded.
  /// </summary>
  public bool Success { get; private set; }

  /// <summary>
  /// The error that occurred during processing (if any).
  /// </summary>
  public Exception? Error { get; private set; }

  /// <summary>
  /// Total duration of message processing.
  /// </summary>
  public TimeSpan TotalDuration { get; set; }

  /// <summary>
  /// Timings for different stages of processing.
  /// Keys are stage names (e.g., "policy-evaluation", "handler-execution").
  /// </summary>
  public Dictionary<string, TimeSpan> Timings { get; } = [];

  /// <summary>
  /// Adds a hop to the trace.
  /// </summary>
  /// <param name="hop">The hop to add</param>
  public void AddHop(MessageHop hop) {
    Hops.Add(hop);
  }

  /// <summary>
  /// Adds a policy decision trail to the trace.
  /// </summary>
  /// <param name="trail">The policy trail to add</param>
  public void AddPolicyTrail(PolicyDecisionTrail trail) {
    PolicyTrails.Add(trail);
  }

  /// <summary>
  /// Sets the outcome of the message processing.
  /// </summary>
  /// <param name="success">Whether processing succeeded</param>
  /// <param name="error">The error that occurred (if any)</param>
  public void SetOutcome(bool success, Exception? error = null) {
    Success = success;
    Error = error;
  }

  /// <summary>
  /// Records timing for a specific stage of processing.
  /// </summary>
  /// <param name="stageName">Name of the stage</param>
  /// <param name="duration">Duration of the stage</param>
  public void RecordTiming(string stageName, TimeSpan duration) {
    Timings[stageName] = duration;
  }
}
