using Microsoft.AspNetCore.SignalR;

namespace ECommerce.BFF.API.Tests.TestHelpers;

/// <summary>
/// Simple mock implementation of IHubContext for testing.
/// Records all SendAsync calls for verification in tests.
/// </summary>
public class MockHubContext<THub> : IHubContext<THub> where THub : Hub {
  private readonly MockHubClients _clients = new();

  public IHubClients Clients => _clients;

  public IGroupManager Groups => throw new NotImplementedException();

  public List<(string Method, object?[] Args)> SentMessages => _clients.SentMessages;
}

public class MockHubClients : IHubClients {
  public List<(string Method, object?[] Args)> SentMessages { get; } = [];

  public IClientProxy All => new MockClientProxy(SentMessages);

  public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) =>
    new MockClientProxy(SentMessages);

  public IClientProxy Client(string connectionId) =>
    new MockClientProxy(SentMessages);

  public IClientProxy Clients(IReadOnlyList<string> connectionIds) =>
    new MockClientProxy(SentMessages);

  public IClientProxy Group(string groupName) =>
    new MockClientProxy(SentMessages);

  public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
    new MockClientProxy(SentMessages);

  public IClientProxy Groups(IReadOnlyList<string> groupNames) =>
    new MockClientProxy(SentMessages);

  public IClientProxy User(string userId) =>
    new MockClientProxy(SentMessages);

  public IClientProxy Users(IReadOnlyList<string> userIds) =>
    new MockClientProxy(SentMessages);
}

public class MockClientProxy(List<(string Method, object?[] Args)> sentMessages) : IClientProxy {
  public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) {
    sentMessages.Add((method, args));
    return Task.CompletedTask;
  }
}
