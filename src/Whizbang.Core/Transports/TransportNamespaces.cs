using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Whizbang.Core.Transports;

/// <summary>
/// The shared TransportNamespace vocabulary (transport traffic classes, topology arc phase 8).
/// A TransportNamespace names a BROKER namespace (an Azure Service Bus namespace, a RabbitMQ
/// connection/vhost) — deliberately distinct from a message's contract (CLR) namespace.
/// Message tags classify traffic; <see cref="Tags.TagOptions.RouteNamespace"/> binds a tag to a
/// TransportNamespace key; the transport maps the key to a client. Unmatched traffic uses
/// <see cref="DefaultKey"/>, so single-namespace hosts are unaffected.
/// </summary>
/// <docs>messaging/transports/transports#transport-namespaces</docs>
/// <tests>tests/Whizbang.Core.Tests/Transports/TransportNamespacesTests.cs</tests>
public static class TransportNamespaces {
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>
  /// The reserved key of the default TransportNamespace — the connection every transport is
  /// registered with today. Multi-namespace connection maps MUST contain this key; traffic
  /// whose type resolves to no routing binding always uses it.
  /// </summary>
  public const string DEFAULT_KEY = "default";

  /// <summary>
  /// The destination-metadata key carrying a resolved TransportNamespace key from the publish
  /// strategy to the transport. Stamped ONLY for non-default classes, so unrouted traffic
  /// keeps today's wire shape byte-identical.
  /// </summary>
  public const string METADATA_KEY = "whizbang.transport-namespace";
#pragma warning restore CA1707

  /// <summary>Gets the reserved default TransportNamespace key (<c>default</c>).</summary>
  public static string DefaultKey => DEFAULT_KEY;

  /// <summary>Gets the destination-metadata key carrying the resolved TransportNamespace key.</summary>
  public static string MetadataKey => METADATA_KEY;

  /// <summary>
  /// True when <paramref name="namespaceKey"/> is the reserved default key (ordinal).
  /// </summary>
  /// <param name="namespaceKey">The TransportNamespace key to test.</param>
  /// <returns><c>true</c> for the default key.</returns>
  public static bool IsDefault(string namespaceKey) =>
    string.Equals(namespaceKey, DEFAULT_KEY, StringComparison.Ordinal);

  /// <summary>
  /// Reads the TransportNamespace key stamped on destination metadata, falling back to
  /// <see cref="DefaultKey"/> when the metadata is null, carries no stamp, or carries a
  /// non-string value. This is the read side transports consult to pick a client.
  /// </summary>
  /// <param name="metadata">The destination metadata (may be null).</param>
  /// <returns>The stamped key, or <see cref="DefaultKey"/>.</returns>
  public static string FromMetadata(IReadOnlyDictionary<string, JsonElement>? metadata) {
    if (metadata is not null
        && metadata.TryGetValue(METADATA_KEY, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } key) {
      return key;
    }

    return DEFAULT_KEY;
  }

  /// <summary>
  /// Returns a copy of <paramref name="destination"/> with <paramref name="namespaceKey"/>
  /// stamped onto its metadata. Address and RoutingKey are untouched by construction — a
  /// TransportNamespace changes WHERE an entity lives (which broker namespace), never WHAT the
  /// entity is called, so a flipped inbox entity name is identical across namespaces.
  /// </summary>
  /// <param name="destination">The destination the routing strategy named.</param>
  /// <param name="namespaceKey">The resolved TransportNamespace key.</param>
  /// <returns>The stamped destination copy.</returns>
  public static TransportDestination Stamp(TransportDestination destination, string namespaceKey) {
    ArgumentNullException.ThrowIfNull(destination);
    ArgumentException.ThrowIfNullOrWhiteSpace(namespaceKey);

    var metadata = new Dictionary<string, JsonElement>();
    if (destination.Metadata is not null) {
      foreach (var (key, value) in destination.Metadata) {
        metadata[key] = value;
      }
    }

    metadata[METADATA_KEY] = JsonElementHelper.FromString(namespaceKey);

    return destination with { Metadata = metadata };
  }
}
