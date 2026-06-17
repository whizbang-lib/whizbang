namespace Whizbang.Core.Messaging;

/// <summary>
/// Thrown by <see cref="CollectiveEventExpander"/> when a collective
/// event's matched-id set exceeds its
/// <see cref="ICollectiveEvent.MaxExpandedInnersAllowed"/> cap. The
/// receiver catches this and either drops the event into the dead-letter
/// queue or skips expansion entirely depending on the consumer's
/// configuration — partial yield is not safe because downstream
/// observers (SignalR, sagas) assume the stream of per-stream markers is
/// a faithful expansion of the matched-set, not a truncation.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
public sealed class CollectiveExpansionLimitExceededException : Exception {
  /// <summary>Assembly-qualified or full type name of the collective event that violated the cap.</summary>
  public string CollectiveTypeName { get; init; } = "unknown-collective";

  /// <summary>The cap declared by the collective event.</summary>
  public int MaxExpandedInnersAllowed { get; init; }

  /// <summary>The matched-set size — the would-be expansion count.</summary>
  public int MatchedStreamCount { get; init; }

  /// <summary>Parameterless ctor required by exception conventions; prefer the rich ctor below.</summary>
  public CollectiveExpansionLimitExceededException() { }

  /// <summary>Single-message ctor required by exception conventions.</summary>
  public CollectiveExpansionLimitExceededException(string message) : base(message) { }

  /// <summary>Message + inner exception ctor required by exception conventions.</summary>
  public CollectiveExpansionLimitExceededException(string message, Exception innerException)
    : base(message, innerException) { }

  /// <summary>Builds the exception with the collective type, cap, and observed count.</summary>
  public CollectiveExpansionLimitExceededException(string collectiveTypeName, int maxAllowed, int matchedStreamCount)
    : base($"Collective event '{collectiveTypeName}' has {matchedStreamCount} matched stream ids, exceeding MaxExpandedInnersAllowed cap of {maxAllowed}. Producer either has a runaway matched-set or the consumer needs to raise the cap on the concrete event type.") {
    CollectiveTypeName = collectiveTypeName;
    MaxExpandedInnersAllowed = maxAllowed;
    MatchedStreamCount = matchedStreamCount;
  }
}
