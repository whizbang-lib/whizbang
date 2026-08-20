using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Whizbang.Core.Transports;

/// <summary>
/// The rail a minted control-class lifetime rides from Core to the broker (transport traffic
/// classes, topology arc phase 9). <c>mint.Checkpoints</c> derives the lifetime, the publish site
/// stamps it onto the destination, and each transport LIFTS it into its native expiry —
/// <c>ServiceBusMessage.TimeToLive</c> on Azure Service Bus, <c>BasicProperties.Expiration</c> on
/// RabbitMQ.
/// </summary>
/// <remarks>
/// <para>
/// Shaped exactly like <see cref="TransportNamespaces"/>: a well-known metadata key, a
/// non-destructive <see cref="Stamp"/> that copies the existing bag (so session key, namespace key
/// and lifetime can be stamped in any order), and a total <see cref="FromMetadata"/> read that
/// degrades to "no lifetime" rather than to a zero-length one.
/// </para>
/// <para>
/// <b>Lifting, not passing through.</b> Both transports copy unrecognized destination metadata
/// into broker application properties / headers. A lifetime left there is inert decoration the
/// broker never reads, so each transport must remove the key as it lifts it — the same treatment
/// <c>StreamId</c> already gets on its way to <c>SessionId</c>.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#control-class</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/CheckpointMintTests.cs:Stamp_ThenRead_RoundTripsAsync</tests>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/AsbControlClassTtlTests.cs:PublishAsync_TtlStampedDestination_SetsMessageTimeToLiveAsync</tests>
/// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQControlClassTtlTests.cs:PublishAsync_TtlStampedDestination_SetsExpirationInMillisecondsAsync</tests>
public static class ControlMessageTtl {
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>
  /// The destination-metadata key carrying a minted lifetime, in whole seconds. Stamped ONLY for
  /// the control class, so unrouted traffic keeps today's wire shape byte-identical.
  /// </summary>
  public const string METADATA_KEY = "whizbang.time-to-live-seconds";
#pragma warning restore CA1707

  /// <summary>Gets the destination-metadata key carrying the minted lifetime.</summary>
  public static string MetadataKey => METADATA_KEY;

  /// <summary>
  /// Reads the minted lifetime stamped on destination metadata, or null when the metadata is
  /// absent, carries no stamp, or carries a value that is not a positive number of seconds.
  /// </summary>
  /// <param name="metadata">The destination metadata (may be null).</param>
  /// <returns>The stamped lifetime, or null for "no lifetime — use the broker default".</returns>
  public static TimeSpan? FromMetadata(IReadOnlyDictionary<string, JsonElement>? metadata) {
    if (metadata is null || !metadata.TryGetValue(METADATA_KEY, out var value)) {
      return null;
    }

    var seconds = value.ValueKind switch {
      JsonValueKind.Number when value.TryGetDouble(out var number) => number,
      JsonValueKind.String when double.TryParse(
        value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
      _ => 0d,
    };

    return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
  }

  /// <summary>
  /// Returns a copy of <paramref name="destination"/> with <paramref name="timeToLive"/> stamped
  /// onto its metadata. Address and RoutingKey are untouched by construction — a lifetime changes
  /// how long an entity holds a message, never which entity it is.
  /// </summary>
  /// <param name="destination">The destination the routing strategy named.</param>
  /// <param name="timeToLive">The minted lifetime; must be strictly positive.</param>
  /// <returns>The stamped destination copy.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The lifetime is zero or negative — an
  /// instantly-dead message is a silent broker drop, never a valid stamp.</exception>
  public static TransportDestination Stamp(TransportDestination destination, TimeSpan timeToLive) {
    ArgumentNullException.ThrowIfNull(destination);
    if (timeToLive <= TimeSpan.Zero) {
      throw new ArgumentOutOfRangeException(
        nameof(timeToLive), timeToLive,
        "A control-class lifetime must be strictly positive — a zero or negative TTL is a message "
        + "the broker discards on arrival, which is a silent drop rather than a delivery policy.");
    }

    var metadata = new Dictionary<string, JsonElement>();
    if (destination.Metadata is not null) {
      foreach (var (key, value) in destination.Metadata) {
        metadata[key] = value;
      }
    }

    metadata[METADATA_KEY] = JsonDocument
      .Parse(timeToLive.TotalSeconds.ToString("R", CultureInfo.InvariantCulture))
      .RootElement.Clone();

    return destination with { Metadata = metadata };
  }

  /// <summary>
  /// Reads the effective lifetime for one batch item: its own per-item stamp when present,
  /// otherwise the shared destination's. Mirrors the collide-by-overwrite contract
  /// <see cref="BulkPublishItem.PerItemMetadata"/> already documents.
  /// </summary>
  /// <param name="perItemMetadata">The item's metadata (may be null).</param>
  /// <param name="destinationMetadata">The shared destination's metadata (may be null).</param>
  /// <returns>The effective lifetime, or null when neither carries a stamp.</returns>
  public static TimeSpan? Resolve(
      IReadOnlyDictionary<string, JsonElement>? perItemMetadata,
      IReadOnlyDictionary<string, JsonElement>? destinationMetadata) =>
    FromMetadata(perItemMetadata) ?? FromMetadata(destinationMetadata);
}
