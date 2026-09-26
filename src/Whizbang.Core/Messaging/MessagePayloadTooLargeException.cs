namespace Whizbang.Core.Messaging;

/// <summary>
/// Thrown when a message's serialized payload is larger than the limit that applies to it, before the
/// message is stored or sent.
/// </summary>
/// <remarks>
/// <para>
/// Every consumer of a message materializes its whole payload, often several times over (the envelope,
/// the deserialized object, a read model built from it). One oversized message can therefore exhaust the
/// memory of every service that receives it, repeatedly, since it is retried. The limit stops it at the
/// producer, where the fix belongs.
/// </para>
/// <para>
/// The fix is almost never to raise the limit. Split the work into one message per item (fan-out), batch
/// items into bounded composite messages (fan-in), or store the body elsewhere and send a reference.
/// </para>
/// </remarks>
/// <docs>fundamentals/messages/payload-size-limit</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/MessagePayloadLimitsTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
  Justification = "The exception is only meaningful with its measurements; the standard constructors would produce one without them.")]
public sealed class MessagePayloadTooLargeException(MessagePayloadSizeContext context, string? userMessage = null, string? errorCode = null)
    : Exception(_format(context, userMessage)) {

  /// <summary>The framework's error code for this failure, recorded on the message in the store.</summary>
  public const string DEFAULT_ERROR_CODE = "WHIZ-PAYLOAD-TOO-LARGE";

  /// <summary>The error code: the application's, when a hook supplied one, otherwise <see cref="DEFAULT_ERROR_CODE"/>.</summary>
  public string ErrorCode { get; } = errorCode ?? DEFAULT_ERROR_CODE;

  /// <summary>A message meant for an end user, when a hook supplied one.</summary>
  public string? UserMessage { get; } = userMessage;

  /// <summary>The message type.</summary>
  public string MessageType { get; } = context?.MessageType ?? "";

  /// <summary>The message id.</summary>
  public Guid MessageId { get; } = context?.MessageId ?? Guid.Empty;

  /// <summary>The stream, when the message has one.</summary>
  public Guid? StreamId { get; } = context?.StreamId;

  /// <summary>The serialized payload's size in UTF-8 bytes.</summary>
  public long PayloadBytes { get; } = context?.PayloadBytes ?? 0;

  /// <summary>The limit that applied.</summary>
  public long LimitBytes { get; } = context?.LimitBytes ?? 0;

  /// <summary>Where that limit came from.</summary>
  public PayloadLimitSource LimitSource { get; } = context?.LimitSource ?? PayloadLimitSource.Default;

  private static string _format(MessagePayloadSizeContext? context, string? userMessage) {
    ArgumentNullException.ThrowIfNull(context);
    return $"{DEFAULT_ERROR_CODE}: {context.MessageType} (message_id={context.MessageId}) has a {context.PayloadBytes:N0}-byte payload, "
      + $"over the {context.LimitBytes:N0}-byte limit from {context.LimitSource}. Split the work into one message per item, batch items "
      + "into bounded composites, or send a reference instead of the body. See fundamentals/messages/payload-size-limit."
      + (userMessage is null ? "" : $" {userMessage}");
  }
}
