using System.Text.Json;

namespace Whizbang.Core.Messaging;

/// <summary>
/// One durable redelivery observation reported by the inbox store: the broker handed this service
/// a message id it had already recorded (topology arc phase 8.5, poison detection layer 2).
/// </summary>
/// <param name="MessageId">The redelivered message id.</param>
/// <param name="ObservationCount">
/// How many times this service has now durably recorded a delivery of that id, including the
/// current one. <c>1</c> is never reported — that is a first sighting, not a redelivery.
/// </param>
/// <docs>fundamentals/dispatcher/routing#poison-messages</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TransportConsumerWorkerPoisonQuarantineTests.cs</tests>
public readonly record struct InboxRedeliveryObservation(Guid MessageId, int ObservationCount) {

  /// <summary>
  /// How many times a receptor has ATTEMPTED this message, or <see langword="null"/> when the store
  /// has no inbox row for it (never stored, or already processed and removed).
  /// </summary>
  /// <remarks>
  /// Distinct from <see cref="ObservationCount"/>, which counts DELIVERIES. A broadcast fanned out to
  /// more subscriptions than the poison bound crosses that bound with an attempt count of zero — no
  /// receptor ever saw it — so quarantining on deliveries alone destroys messages that never failed.
  /// Null means UNMEASURED and is never read as "zero failures so far".
  /// </remarks>
  public int? ProcessingAttempts { get; init; }

  /// <summary>
  /// Parses the compact JSON projection both Postgres coordinators fetch from
  /// <c>store_inbox_messages</c>: <c>[{"m":"&lt;uuid&gt;","o":3}, …]</c>. A single scalar keeps the
  /// read engine-agnostic (Dapper scalar / EF raw command) and avoids adding a keyless entity type
  /// to the model for one diagnostic projection. Hand-parsed with
  /// <see cref="JsonDocument"/> — no reflection, AOT-safe.
  /// </summary>
  /// <param name="json">The JSON array text; null/empty yields an empty list.</param>
  /// <returns>Observations with a count above one, in document order.</returns>
  public static IReadOnlyList<InboxRedeliveryObservation> ParseProjection(string? json) {
    if (string.IsNullOrWhiteSpace(json)) {
      return [];
    }

    using var document = JsonDocument.Parse(json);
    if (document.RootElement.ValueKind != JsonValueKind.Array) {
      return [];
    }

    var observations = new List<InboxRedeliveryObservation>();
    foreach (var element in document.RootElement.EnumerateArray()) {
      if (!element.TryGetProperty("m", out var id)
          || !element.TryGetProperty("o", out var count)
          || !Guid.TryParse(id.GetString(), out var messageId)
          || !count.TryGetInt32(out var observationCount)
          || observationCount <= 1) {
        continue;
      }
      observations.Add(new InboxRedeliveryObservation(messageId, observationCount));
    }
    return observations;
  }
}
