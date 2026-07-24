using Microsoft.AspNetCore.SignalR;

namespace Whizbang.SignalR.Tests;

/// <summary>
/// Tests for <see cref="SignalRTokenRefreshNotifier{THub}"/> — the SignalR default
/// implementation of <see cref="Whizbang.Core.Security.ITokenRefreshNotifier"/>.
/// </summary>
/// <tests>SignalRTokenRefreshNotifier,TokenRefreshPayload</tests>
[Category("SignalR")]
public class SignalRTokenRefreshNotifierTests {
  [Test]
  public async Task NotifyAsync_SendsRefreshTokenRequested_ToTargetUserAsync() {
    var sent = new List<(string UserId, string Method, object?[] Args)>();
    var hubContext = new CapturingHubContext<TestHub>(sent);
    var notifier = new SignalRTokenRefreshNotifier<TestHub>(hubContext);

    await notifier.NotifyAsync("user-42", "permissions-changed");

    var count = sent.Count;
    var (UserId, Method, _) = sent[0];
    var method = Method;
    var userId = UserId;
    await Assert.That(count).IsEqualTo(1);
    await Assert.That(method).IsEqualTo("RefreshTokenRequested");
    await Assert.That(userId).IsEqualTo("user-42");
  }

  [Test]
  public async Task NotifyAsync_PayloadCarriesReason_AndTimestampAsync() {
    var sent = new List<(string UserId, string Method, object?[] Args)>();
    var hubContext = new CapturingHubContext<TestHub>(sent);
    var notifier = new SignalRTokenRefreshNotifier<TestHub>(hubContext);
    var before = DateTimeOffset.UtcNow;

    await notifier.NotifyAsync("user-42", "tenant-renamed");

    var (_, _, Args) = sent[0];
    var payload = (TokenRefreshPayload)Args[0]!;
    var reason = payload.Reason;
    var ts = payload.Timestamp;
    await Assert.That(reason).IsEqualTo("tenant-renamed");
    await Assert.That(ts >= before).IsTrue();
  }

  [Test]
  public async Task TokenRefreshPayload_ConstructionAsync() {
    var ts = DateTimeOffset.UtcNow;
    var payload = new TokenRefreshPayload("foo", ts);
    var reason = payload.Reason;
    var got = payload.Timestamp;
    await Assert.That(reason).IsEqualTo("foo");
    await Assert.That(got).IsEqualTo(ts);
  }

  // ===== Test hub + capturing mock =====

  public sealed class TestHub : Hub { }

  private sealed class CapturingHubContext<THub>(List<(string UserId, string Method, object?[] Args)> sent)
      : IHubContext<THub> where THub : Hub {
    public IHubClients Clients { get; } = new CapturingClients(sent);
    public IGroupManager Groups => throw new NotImplementedException();
  }

  private sealed class CapturingClients(List<(string UserId, string Method, object?[] Args)> sent) : IHubClients {
    private readonly List<(string UserId, string Method, object?[] Args)> _sent = sent;
    public IClientProxy All => throw new NotImplementedException();
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
    public IClientProxy Client(string connectionId) => throw new NotImplementedException();
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotImplementedException();
    public IClientProxy Group(string groupName) => throw new NotImplementedException();
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotImplementedException();
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
    public IClientProxy User(string userId) => new CapturingClientProxy(userId, _sent);
    public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotImplementedException();
  }

  private sealed class CapturingClientProxy(string userId, List<(string UserId, string Method, object?[] Args)> sent) : IClientProxy {
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) {
      sent.Add((userId, method, args));
      return Task.CompletedTask;
    }
  }
}
