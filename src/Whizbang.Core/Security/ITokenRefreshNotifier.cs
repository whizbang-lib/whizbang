namespace Whizbang.Core.Security;

/// <summary>
/// Sends a "your JWT claims may have changed, please re-acquire" signal to a connected
/// user. Transport-agnostic — Whizbang ships a SignalR default implementation, but
/// custom transports (Pusher, server-sent events, polling) can plug in by implementing
/// this interface.
/// </summary>
/// <remarks>
/// <para>
/// The notification is intentionally light: it carries no claim payload. The recipient
/// is expected to call its identity provider's standard refresh endpoint and pick up
/// the new claims via that path. Embedding claims in the notification would create a
/// second source of truth for authentication state and complicate session management.
/// </para>
/// <para>
/// Producers: anything that mutates state visible in a JWT — permission grants/revokes,
/// group membership changes, identity-level claim updates, account deactivation,
/// tenant rename. Consumers should send this whenever a state change implies the
/// previously issued token is now stale.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/token-refresh</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/ITokenRefreshNotifierTests.cs</tests>
public interface ITokenRefreshNotifier {
  /// <summary>
  /// Notifies the connected user that their token may be stale and should be refreshed.
  /// </summary>
  /// <param name="userId">User identifier (typically the JWT <c>sub</c>) of the recipient.</param>
  /// <param name="reason">
  /// Producer-supplied reason for diagnostics/telemetry (e.g., "permissions-changed",
  /// "tenant-renamed"). Not consumed by clients.
  /// </param>
  /// <param name="cancellationToken">Cancellation token.</param>
  ValueTask NotifyAsync(string userId, string reason, CancellationToken cancellationToken = default);
}
