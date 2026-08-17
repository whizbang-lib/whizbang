using System.Text.Json;

namespace Whizbang.Core.Transports;

/// <summary>
/// Builds <see cref="TransportDestination"/>s for DIRECT transport publishes (the integrity /
/// control-plane surface that bypasses the outbox). Session-enabled subscriptions dead-letter
/// every message that carries no session, so a direct publish MUST stamp its stream identity
/// into the destination metadata — the same <c>StreamId</c> key the outbox publish path uses,
/// which session-ordering transports translate into the broker session id.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/IntegrityCheckpointWorkerTests.cs</tests>
public static class ControlPlaneDestination {
  /// <summary>A destination for <paramref name="address"/> carrying <paramref name="sessionStreamId"/> as the session key.</summary>
  public static TransportDestination For(string address, Guid sessionStreamId) =>
    WithSession(new TransportDestination(address), sessionStreamId);

  /// <summary>
  /// A destination for <paramref name="address"/> carrying the session key AND the conventional
  /// "{namespace}.{typename}" Subject for <paramref name="payloadType"/>. Shared-inbox
  /// subscriptions filter on the Subject (sys.Label) by namespace — a publish with no routing
  /// key gets the literal Subject "message" and is silently dropped by the broker rule (no logs,
  /// no dead-letter). Every directed control-plane publish to a filtered topic uses this form.
  /// </summary>
  public static TransportDestination For(string address, Guid sessionStreamId, Type payloadType) {
    ArgumentNullException.ThrowIfNull(payloadType);
    var routingKey = $"{payloadType.Namespace!.ToLowerInvariant()}.{payloadType.Name.ToLowerInvariant()}";
    return WithSession(new TransportDestination(address, routingKey), sessionStreamId);
  }

  /// <summary>The same destination with the session key added (Address and RoutingKey preserved).</summary>
  public static TransportDestination WithSession(TransportDestination destination, Guid sessionStreamId) {
    ArgumentNullException.ThrowIfNull(destination);
    return destination with {
      Metadata = new Dictionary<string, JsonElement> {
        ["StreamId"] = JsonDocument.Parse($"\"{sessionStreamId}\"").RootElement,
      },
    };
  }
}
