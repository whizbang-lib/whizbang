using System.Text.Json;

namespace Whizbang.Core.Transports;

/// <summary>
/// Builds <see cref="TransportDestination"/>s for DIRECT transport publishes (the integrity /
/// control-plane surface that bypasses the outbox). Session-enabled subscriptions dead-letter
/// every message that carries no session, so a direct publish MUST stamp its stream identity
/// into the destination metadata — the same <c>StreamId</c> key the outbox publish path uses,
/// which session-ordering transports translate into the broker session id.
/// </summary>
/// <docs>proposals/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/IntegrityCheckpointWorkerTests.cs</tests>
public static class ControlPlaneDestination {
  /// <summary>A destination for <paramref name="address"/> carrying <paramref name="sessionStreamId"/> as the session key.</summary>
  public static TransportDestination For(string address, Guid sessionStreamId) =>
    WithSession(new TransportDestination(address), sessionStreamId);

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
