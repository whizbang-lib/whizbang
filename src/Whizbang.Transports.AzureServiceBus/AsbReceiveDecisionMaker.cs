using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Policy that decides what to do with an inbound ASB message based purely on the data it
/// carries — without touching the broker's ack/dlq APIs. Extracted from the legacy
/// <c>_deserializeReceivedMessageAsync</c> so unit tests can drive behavior without
/// needing real <c>ProcessMessageEventArgs</c> instances.
/// </summary>
/// <remarks>
/// Slice 1 hotfix scope: type-resolution failures (<c>MissingJsonTypeInfo</c>,
/// <c>DeserializationFailed</c>) become <see cref="AsbReceiveAction.AckAndDrop"/> instead of
/// the broker-coupled DLQ. Operators see a structured warning log; messages exit the topic
/// without accumulating in ASB DLQ. Genuine broker-metadata failures
/// (<c>MissingEnvelopeType</c>) still route to <see cref="AsbReceiveAction.DeadLetter"/>.
/// Stub today; GREEN commit lands the implementation.
/// </remarks>
[SuppressMessage("Performance", "CA1822:Mark members as static",
  Justification = "Instance method enables DI registration as singleton; future revisions may inject ILogger / counters.")]
internal sealed class AsbReceiveDecisionMaker {
  /// <summary>Evaluates the inbound message and returns the action + envelope (when applicable).</summary>
  public AsbReceiveDecision Decide(
      IReadOnlyDictionary<string, object> applicationProperties,
      string bodyJson,
      System.Func<string, JsonSerializerOptions, JsonTypeInfo?> getTypeInfoByName,
      JsonSerializerOptions jsonOptions) {
    return new AsbReceiveDecision {
      Action = AsbReceiveAction.DeadLetter,
      Reason = "NotImplemented",
      Description = "Stub — implementation lands in GREEN commit",
    };
  }
}
