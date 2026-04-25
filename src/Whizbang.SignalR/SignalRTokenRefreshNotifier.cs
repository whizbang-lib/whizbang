using Microsoft.AspNetCore.SignalR;
using Whizbang.Core.Security;

namespace Whizbang.SignalR;

/// <summary>
/// SignalR default implementation of <see cref="ITokenRefreshNotifier"/>. Sends a
/// <c>RefreshTokenRequested</c> SignalR message to the connected user identified by the
/// <c>userId</c> argument to <see cref="NotifyAsync"/>. The hub type is supplied as a
/// generic parameter so consumers can target whichever hub they expose to clients.
/// </summary>
/// <remarks>
/// <para>
/// Register in DI:
/// <code>
/// services.AddSingleton&lt;ITokenRefreshNotifier, SignalRTokenRefreshNotifier&lt;MyHub&gt;&gt;();
/// </code>
/// </para>
/// <para>
/// Clients listen for <c>RefreshTokenRequested</c> and call their identity provider's
/// refresh endpoint to pick up new claims.
/// </para>
/// </remarks>
/// <typeparam name="THub">The SignalR hub clients are connected to.</typeparam>
/// <docs>fundamentals/security/token-refresh#signalr-default</docs>
/// <tests>tests/Whizbang.SignalR.Tests/SignalRTokenRefreshNotifierTests.cs</tests>
public sealed class SignalRTokenRefreshNotifier<THub>(IHubContext<THub> hubContext) : ITokenRefreshNotifier
    where THub : Hub {
  private readonly IHubContext<THub> _hubContext = hubContext;

  /// <inheritdoc />
  public async ValueTask NotifyAsync(string userId, string reason, CancellationToken cancellationToken = default) {
    var payload = new TokenRefreshPayload(reason, DateTimeOffset.UtcNow);
    await _hubContext.Clients
      .User(userId)
      .SendAsync("RefreshTokenRequested", payload, cancellationToken);
  }
}

/// <summary>Payload sent on the <c>RefreshTokenRequested</c> SignalR message.</summary>
/// <param name="Reason">
/// Producer-supplied reason (e.g., "permissions-changed", "tenant-renamed"). Diagnostics only.
/// </param>
/// <param name="Timestamp">When the notifier emitted the message.</param>
public sealed record TokenRefreshPayload(string Reason, DateTimeOffset Timestamp);
