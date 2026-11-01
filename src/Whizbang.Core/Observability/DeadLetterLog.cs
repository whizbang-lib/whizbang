// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Observability;

/// <summary>
/// The one record every dead-letter path logs for a message it dead-letters (#1901): EventId
/// <see cref="EVENT_ID"/>, named <see cref="EVENT_NAME"/>, with the message's id and type, why it was dead-lettered and
/// its last failure, as the exception when one is in hand or as the recorded error text when it is not.
/// </summary>
/// <remarks>
/// <para>A failure that will be retried is logged at Warning by the path that saw it. The dead-letter record is the
/// terminal one: the message's effect will not happen unless it is recovered, so the record is logged at the level the
/// dead-letter logging options choose, Error by default. One record is logged per message, by the path that moved it;
/// a path that finds the message already moved logs none.</para>
/// <para>Every path logs the same EventId and name under its own category, so one query (or one alert) on the event
/// finds the dead letters of the inbox, the outbox, the perspective worker, the transport consumer, the batchers and
/// the broker dead-letter drains together.</para>
/// </remarks>
/// <docs>operations/observability/logging-categories#dead-lettered-messages</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerDeadLetterLogTests.cs</tests>
public static partial class DeadLetterLog {
  /// <summary>The EventId of the dead-letter record.</summary>
  public const int EVENT_ID = 1901;

  /// <summary>The EventId name of the dead-letter record.</summary>
  public const string EVENT_NAME = "MessageDeadLettered";

  private const string UNKNOWN_TYPE = "unknown";
  private const string NONE_RECORDED = "none recorded";
  private const string NO_BROKER_REASON = "none given by the broker";

  /// <summary>
  /// Logs the dead-letter record for one message.
  /// </summary>
  /// <param name="logger">The dead-lettering path's logger.</param>
  /// <param name="level">The level to log at; the dead-letter logging options choose it.</param>
  /// <param name="exception">The last failure's exception, when the path has it; null when only its text survives.</param>
  /// <param name="messageId">The dead-lettered message.</param>
  /// <param name="messageType">The message's type, or null when the path cannot tell (the record says <c>unknown</c>).</param>
  /// <param name="reason">Why it was dead-lettered, typically a <see cref="Messaging.MessageFailureReason"/> name.</param>
  /// <param name="lastError">The last failure's recorded text, or null when none was recorded (the record says so).</param>
  /// <docs>operations/observability/logging-categories#dead-lettered-messages</docs>
  /// <tests>tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerDeadLetterLogTests.cs:MaxAttempts_MovedToTheDeadLetterStore_LogsOneErrorRecordWithTypeAndLastErrorAsync</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMqDeadLetterDrainerTests.cs:Drain_ImportedWithoutTypeOrReason_RecordSaysSoAsync</tests>
  public static void MessageDeadLettered(
      ILogger logger, LogLevel level, Exception? exception, Guid messageId, string? messageType, string reason, string? lastError) =>
    LogDeadLettered(logger, level, exception, messageId, messageType ?? UNKNOWN_TYPE, reason, lastError ?? NONE_RECORDED);

  /// <summary>
  /// Logs the dead-letter record for a message taken into dead-letter custody through a
  /// <see cref="BrokerDeadLetterImport"/>: a broker dead letter a drain imported, or a received message the consumer
  /// could not store. The type is the payload type the envelope type names, the reason is the import's, and the last
  /// error is its description followed by where the message came from.
  /// </summary>
  /// <param name="logger">The importing path's logger.</param>
  /// <param name="level">The level to log at; the dead-letter logging options choose it.</param>
  /// <param name="import">The import that took custody.</param>
  /// <param name="exception">The exception that sent the message to custody, when the path has one.</param>
  /// <docs>operations/observability/logging-categories#dead-lettered-messages</docs>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AzureServiceBusDeadLetterDrainerTests.cs:DrainDeadLetterQueueAsync_Imported_LogsTheDeadLetterRecordAtErrorAsync</tests>
  /// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMqDeadLetterDrainerTests.cs:Drain_ImportedWithoutTypeOrReason_RecordSaysSoAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportConsumerWorkerUnstorableMessageTests.cs:SerializationFails_LogsTheDeadLetterRecordAtErrorWithTypeAndCauseAsync</tests>
  public static void BrokerMessageDeadLettered(ILogger logger, LogLevel level, BrokerDeadLetterImport import, Exception? exception = null) {
    ArgumentNullException.ThrowIfNull(import);
    MessageDeadLettered(logger, level, exception, import.MessageId, MessageTypeOf(import.MessageType),
      import.BrokerReason ?? NO_BROKER_REASON, $"{import.BrokerDescription ?? NONE_RECORDED} (from {import.Destination})");
  }

  /// <summary>
  /// The payload type an envelope type name carries, the envelope type name itself when it does not name a payload,
  /// or null when there is none.
  /// </summary>
  internal static string? MessageTypeOf(string? envelopeType) =>
    envelopeType is null ? null : EnvelopeTypeNameHelper.ExtractInnerTypeName(envelopeType) ?? envelopeType;

  [LoggerMessage(EventId = EVENT_ID, EventName = EVENT_NAME,
    Message = "Message {MessageId} of type {MessageType} was dead-lettered ({DeadLetterReason}) and will not be processed unless it is recovered. Last error: {LastError}")]
  private static partial void LogDeadLettered(
    ILogger logger, LogLevel level, Exception? exception, Guid messageId, string messageType, string deadLetterReason, string lastError);
}
