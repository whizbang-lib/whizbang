namespace Whizbang.Core.Messaging;

/// <summary>Where the limit applied to a message came from.</summary>
/// <docs>fundamentals/messages/payload-size-limit</docs>
public enum PayloadLimitSource {
  /// <summary><c>WhizbangCoreOptions.MaxMessagePayloadBytes</c>.</summary>
  Default = 0,

  /// <summary>The message type's <c>[MaxPayloadSize]</c>.</summary>
  MessageType = 1,

  /// <summary>The dispatch's own <c>DispatchOptions.WithMaxPayloadBytes</c>.</summary>
  Call = 2,
}

/// <summary>What a payload-size hook is told about one message.</summary>
/// <param name="MessageType">The message's type name.</param>
/// <param name="MessageId">The message id.</param>
/// <param name="StreamId">The stream, when the message has one.</param>
/// <param name="PayloadBytes">The serialized payload's size in UTF-8 bytes, as it will be stored and sent.</param>
/// <param name="LimitBytes">The limit that applies.</param>
/// <param name="LimitSource">Where that limit came from.</param>
/// <param name="OverLimit">True when the payload exceeds the limit; false when it only crossed the warning threshold.</param>
/// <docs>fundamentals/messages/payload-size-limit#hooks</docs>
public sealed record MessagePayloadSizeContext(
  string MessageType, Guid MessageId, Guid? StreamId, long PayloadBytes, long LimitBytes,
  PayloadLimitSource LimitSource, bool OverLimit);

/// <summary>A hook's answer about one message.</summary>
/// <docs>fundamentals/messages/payload-size-limit#hooks</docs>
public readonly record struct MessagePayloadSizeDecision {
  /// <summary>True to allow, false to reject, null for no opinion.</summary>
  public bool? Verdict { get; private init; }

  /// <summary>A message meant for an end user, carried on the exception when rejected.</summary>
  public string? UserMessage { get; private init; }

  /// <summary>An application error code carried on the exception when rejected, instead of the framework's.</summary>
  public string? ErrorCode { get; private init; }

  /// <summary>No opinion: the limit decides.</summary>
  public static MessagePayloadSizeDecision NoOpinion => default;

  /// <summary>Allow this message even though it is over the limit.</summary>
  public static MessagePayloadSizeDecision Allow() => new() { Verdict = true };

  /// <summary>Reject this message, with what to tell the end user and, optionally, the application's own error code.</summary>
  public static MessagePayloadSizeDecision Reject(string userMessage, string? errorCode = null) =>
    new() { Verdict = false, UserMessage = userMessage, ErrorCode = errorCode };
}

/// <summary>
/// Sees every message whose payload crosses the warning threshold or the limit, before the framework acts.
/// </summary>
/// <remarks>
/// For end-user feedback and validation: a hook can reject a message (even one under the limit) with a
/// message meant for the person who caused it, allow a known-large message over the limit, or only record
/// it. Several hooks may be registered; a rejection from any wins, then an allowance, then the limit.
/// </remarks>
/// <docs>fundamentals/messages/payload-size-limit#hooks</docs>
public interface IMessagePayloadSizeHook {
  /// <summary>Decides about one message.</summary>
  MessagePayloadSizeDecision Evaluate(MessagePayloadSizeContext context);
}
