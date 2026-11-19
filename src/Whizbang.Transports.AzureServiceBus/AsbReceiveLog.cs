// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// The receive path's account of each delivery (#2406). Three lines, read together, tell "the handler never saw this
/// delivery" from "the handler saw it and did not settle it" for any one message:
/// <list type="bullet">
///   <item><description><see cref="Redelivered"/> (Information): a delivery arrived with a delivery count above 1, so an
///   earlier delivery of it was not settled, wherever that happened.</description></item>
///   <item><description><see cref="HandedToHandler"/> (Debug): this delivery reached the handler.</description></item>
///   <item><description><see cref="ReturnedUnsettled"/> (Warning): this delivery went back to the broker without being
///   completed or dead-lettered, and why.</description></item>
/// </list>
/// A delivery count that rises with no matching handed-to-handler line was lost before this code saw it.
/// </summary>
/// <remarks>
/// Source-generated, so each line checks its level before anything is formatted: with Debug off the hot path pays one
/// level check per delivery and allocates nothing.
/// </remarks>
/// <docs>messaging/transports/azure-service-bus#receive-diagnostics</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Component.Tests/AzureServiceBusReceiveDiagnosticsTests.cs</tests>
internal static partial class AsbReceiveLog {
  /// <summary>Reason: the subscription is paused or disposed, so the delivery is abandoned without being handled.</summary>
  internal const string PAUSED_OR_DISPOSED =
    "the subscription is paused or disposed, so it was abandoned without being handled";

  /// <summary>Reason: the handler failed, so the delivery is abandoned for the broker to redeliver.</summary>
  internal const string HANDLER_FAILED = "the handler failed, so it was abandoned for redelivery";

  /// <summary>The name a delivery's type is reported under when it carries no envelope type.</summary>
  internal const string UNKNOWN_TYPE = "unknown";

  /// <summary>A delivery went back to the broker unsettled: neither completed nor dead-lettered.</summary>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Component.Tests/AzureServiceBusReceiveDiagnosticsTests.cs:SessionBatch_PausedSubscription_LogsUnsettledWarningNamingTheMessageAsync</tests>
  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Message {MessageId} of type {MessageType} on {TopicName}/{SubscriptionName} was returned to the broker "
      + "unsettled at delivery {DeliveryCount}: {Reason}")]
  internal static partial void ReturnedUnsettled(ILogger logger, string messageId, string messageType, string topicName,
    string subscriptionName, int deliveryCount, string reason);

  /// <summary>A delivery is about to be handed to the handler.</summary>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Component.Tests/AzureServiceBusReceiveDiagnosticsTests.cs:HandsADeliveryToTheHandler_WithDebugOff_FormatsNothingAsync</tests>
  [LoggerMessage(Level = LogLevel.Debug,
    Message = "Handing message {MessageId} at delivery {DeliveryCount} from {TopicName}/{SubscriptionName} to the handler")]
  internal static partial void HandedToHandler(ILogger logger, string messageId, int deliveryCount, string topicName,
    string subscriptionName);

  /// <summary>A delivery arrived with a delivery count above 1.</summary>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Component.Tests/AzureServiceBusReceiveDiagnosticsTests.cs:Redelivery_HandledNormally_WritesOneInformationLineNamingItAsync</tests>
  [LoggerMessage(Level = LogLevel.Information,
    Message = "Message {MessageId} of type {MessageType} arrived on {TopicName}/{SubscriptionName} at delivery "
      + "{DeliveryCount}: an earlier delivery was not settled")]
  internal static partial void Redelivered(ILogger logger, string messageId, string messageType, string topicName,
    string subscriptionName, int deliveryCount);

  /// <summary>
  /// The delivery's message type for a log line: the envelope's payload type without its assembly, the named type itself
  /// when it is not an envelope name, or <see cref="UNKNOWN_TYPE"/> when the delivery names none. Called only once a line
  /// is known to be written.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Component.Tests/AzureServiceBusReceiveDiagnosticsTests.cs:NonSessionBatch_PausedSubscription_LogsUnsettledWarningNamingTheMessageAsync</tests>
  /// <tests>tests/Whizbang.Transports.AzureServiceBus.Component.Tests/AzureServiceBusReceiveDiagnosticsTests.cs:Session_PausedSubscription_LogsUnsettledWarningNamingTheMessageAsync</tests>
  internal static string MessageTypeOf(ServiceBusReceivedMessage message) {
    if (!message.ApplicationProperties.TryGetValue(AsbMessageHeaderReader.ENVELOPE_TYPE_PROPERTY_KEY, out var value)
        || value is not string envelopeType
        || envelopeType.Length == 0) {
      return UNKNOWN_TYPE;
    }
    return TypeNameFormatter.GetFullName(EnvelopeTypeNameHelper.ExtractInnerTypeName(envelopeType) ?? envelopeType);
  }
}
