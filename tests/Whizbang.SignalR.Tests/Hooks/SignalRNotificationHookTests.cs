using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;
using Whizbang.SignalR.Hooks;

namespace Whizbang.SignalR.Tests.Hooks;

/// <summary>
/// Tests for <see cref="SignalRNotificationHook{THub}"/>.
/// Validates SignalR notification sending for tagged messages.
/// </summary>
/// <tests>Whizbang.SignalR/Hooks/SignalRNotificationHook.cs</tests>
[Category("SignalR")]
[Category("Hooks")]
public class SignalRNotificationHookTests {
  [Test]
  public async Task OnTaggedMessage_SendsToAllClients_WhenNoGroupSpecifiedAsync() {
    // Arrange
    var sentNotifications = new List<(string Method, object? Notification)>();
    var mockClients = new MockHubClients(sentNotifications);
    var mockHubContext = new MockHubContext<TestHub>(mockClients);
    var hook = new SignalRNotificationHook<TestHub>(mockHubContext);

    var attribute = new NotificationTagAttribute {
      Tag = "test-notification",
      Priority = NotificationPriority.Normal
    };
    var message = new TestOrderEvent { OrderId = Guid.NewGuid(), Amount = 100m };
    var payload = JsonSerializer.SerializeToElement(message);
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestOrderEvent),
      Payload = payload
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
    await Assert.That(sentNotifications.Count).IsEqualTo(1);
    await Assert.That(sentNotifications[0].Method).IsEqualTo("ReceiveNotification");
  }

  [Test]
  public async Task OnTaggedMessage_SendsToGroup_WhenGroupSpecifiedAsync() {
    // Arrange
    var sentNotifications = new List<(string Method, object? Notification, string? Group)>();
    var mockClients = new MockHubClientsWithGroup(sentNotifications);
    var mockHubContext = new MockHubContext<TestHub>(mockClients);
    var hook = new SignalRNotificationHook<TestHub>(mockHubContext);

    var attribute = new NotificationTagAttribute {
      Tag = "order-shipped",
      Group = "customer-group",
      Priority = NotificationPriority.High
    };
    var message = new TestOrderEvent { OrderId = Guid.NewGuid(), Amount = 250m };
    var payload = JsonSerializer.SerializeToElement(message);
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestOrderEvent),
      Payload = payload
    };

    // Act
    var result = await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
    await Assert.That(sentNotifications.Count).IsEqualTo(1);
    await Assert.That(sentNotifications[0].Group).IsEqualTo("customer-group");
  }

  [Test]
  public async Task OnTaggedMessage_ResolvesGroupPlaceholders_FromPayloadAsync() {
    // Arrange
    var sentNotifications = new List<(string Method, object? Notification, string? Group)>();
    var mockClients = new MockHubClientsWithGroup(sentNotifications);
    var mockHubContext = new MockHubContext<TestHub>(mockClients);
    var hook = new SignalRNotificationHook<TestHub>(mockHubContext);

    var customerId = Guid.NewGuid();
    var attribute = new NotificationTagAttribute {
      Tag = "order-update",
      Group = "customer-{CustomerId}",
      Priority = NotificationPriority.Normal
    };
    var message = new TestCustomerEvent { CustomerId = customerId, Name = "Test Customer" };
    var payload = JsonSerializer.SerializeToElement(message);
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestCustomerEvent),
      Payload = payload
    };

    // Act
    await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(sentNotifications[0].Group).IsEqualTo($"customer-{customerId}");
  }

  [Test]
  public async Task OnTaggedMessage_ResolvesGroupPlaceholders_FromScopeAsync() {
    // Arrange
    var sentNotifications = new List<(string Method, object? Notification, string? Group)>();
    var mockClients = new MockHubClientsWithGroup(sentNotifications);
    var mockHubContext = new MockHubContext<TestHub>(mockClients);
    var hook = new SignalRNotificationHook<TestHub>(mockHubContext);

    var attribute = new NotificationTagAttribute {
      Tag = "tenant-notification",
      Group = "tenant-{TenantId}",
      Priority = NotificationPriority.Normal
    };
    var message = new TestOrderEvent { OrderId = Guid.NewGuid(), Amount = 100m };
    var payload = JsonSerializer.SerializeToElement(message);
    var scope = new Dictionary<string, object?> {
      { "TenantId", "tenant-123" }
    };
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestOrderEvent),
      Payload = payload,
      Scope = scope
    };

    // Act
    await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(sentNotifications[0].Group).IsEqualTo("tenant-tenant-123");
  }

  [Test]
  public async Task OnTaggedMessage_IncludesCorrectPriority_InNotificationAsync() {
    // Arrange
    var sentNotifications = new List<(string Method, NotificationMessage? Notification)>();
    var mockClients = new MockHubClientsCapture(sentNotifications);
    var mockHubContext = new MockHubContext<TestHub>(mockClients);
    var hook = new SignalRNotificationHook<TestHub>(mockHubContext);

    var attribute = new NotificationTagAttribute {
      Tag = "critical-alert",
      Priority = NotificationPriority.Critical
    };
    var message = new TestOrderEvent { OrderId = Guid.NewGuid(), Amount = 100m };
    var payload = JsonSerializer.SerializeToElement(message);
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestOrderEvent),
      Payload = payload
    };

    // Act
    await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(sentNotifications[0].Notification!.Priority).IsEqualTo("Critical");
    await Assert.That(sentNotifications[0].Notification!.Tag).IsEqualTo("critical-alert");
  }

  [Test]
  public async Task OnTaggedMessage_IncludesMessageType_InNotificationAsync() {
    // Arrange
    var sentNotifications = new List<(string Method, NotificationMessage? Notification)>();
    var mockClients = new MockHubClientsCapture(sentNotifications);
    var mockHubContext = new MockHubContext<TestHub>(mockClients);
    var hook = new SignalRNotificationHook<TestHub>(mockHubContext);

    var attribute = new NotificationTagAttribute {
      Tag = "test-tag",
      Priority = NotificationPriority.Normal
    };
    var message = new TestOrderEvent { OrderId = Guid.NewGuid(), Amount = 100m };
    var payload = JsonSerializer.SerializeToElement(message);
    var context = new TagContext<NotificationTagAttribute> {
      Attribute = attribute,
      Message = message,
      MessageType = typeof(TestOrderEvent),
      Payload = payload
    };

    // Act
    await hook.OnTaggedMessageAsync(context, CancellationToken.None);

    // Assert
    await Assert.That(sentNotifications[0].Notification!.MessageType).IsEqualTo("TestOrderEvent");
  }

  [Test]
  public async Task Constructor_ThrowsArgumentNullException_WhenHubContextIsNullAsync() {
    // Act & Assert
    await Assert.That(() => new SignalRNotificationHook<TestHub>(null!)).Throws<ArgumentNullException>();
  }

  // Test types
  private sealed record TestOrderEvent {
    public required Guid OrderId { get; init; }
    public required decimal Amount { get; init; }
  }

  private sealed record TestCustomerEvent {
    public required Guid CustomerId { get; init; }
    public required string Name { get; init; }
  }

  // Test hub
  private sealed class TestHub : Hub { }

  // Mock implementations
  private sealed class MockHubContext<THub> : IHubContext<THub> where THub : Hub {
    public IHubClients Clients { get; }
    public IGroupManager Groups => throw new NotImplementedException();

    public MockHubContext(IHubClients clients) {
      Clients = clients;
    }
  }

  private sealed class MockHubClients : IHubClients {
    private readonly List<(string Method, object? Notification)> _sent;

    public MockHubClients(List<(string, object?)> sent) {
      _sent = sent;
    }

    public IClientProxy All => new MockClientProxy(_sent);
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
    public IClientProxy Client(string connectionId) => throw new NotImplementedException();
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotImplementedException();
    public IClientProxy Group(string groupName) => throw new NotImplementedException();
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotImplementedException();
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
    public IClientProxy User(string userId) => throw new NotImplementedException();
    public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotImplementedException();
  }

  private sealed class MockHubClientsWithGroup : IHubClients {
    private readonly List<(string Method, object? Notification, string? Group)> _sent;

    public MockHubClientsWithGroup(List<(string, object?, string?)> sent) {
      _sent = sent;
    }

    public IClientProxy All => new MockClientProxyWithGroup(_sent, null);
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
    public IClientProxy Client(string connectionId) => throw new NotImplementedException();
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotImplementedException();
    public IClientProxy Group(string groupName) => new MockClientProxyWithGroup(_sent, groupName);
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotImplementedException();
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
    public IClientProxy User(string userId) => throw new NotImplementedException();
    public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotImplementedException();
  }

  private sealed class MockHubClientsCapture : IHubClients {
    private readonly List<(string Method, NotificationMessage? Notification)> _sent;

    public MockHubClientsCapture(List<(string, NotificationMessage?)> sent) {
      _sent = sent;
    }

    public IClientProxy All => new MockClientProxyCapture(_sent);
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
    public IClientProxy Client(string connectionId) => throw new NotImplementedException();
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotImplementedException();
    public IClientProxy Group(string groupName) => new MockClientProxyCapture(_sent);
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotImplementedException();
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
    public IClientProxy User(string userId) => throw new NotImplementedException();
    public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotImplementedException();
  }

  private sealed class MockClientProxy : IClientProxy {
    private readonly List<(string Method, object? Notification)> _sent;

    public MockClientProxy(List<(string, object?)> sent) {
      _sent = sent;
    }

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) {
      _sent.Add((method, args.Length > 0 ? args[0] : null));
      return Task.CompletedTask;
    }
  }

  private sealed class MockClientProxyWithGroup : IClientProxy {
    private readonly List<(string Method, object? Notification, string? Group)> _sent;
    private readonly string? _group;

    public MockClientProxyWithGroup(List<(string, object?, string?)> sent, string? group) {
      _sent = sent;
      _group = group;
    }

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) {
      _sent.Add((method, args.Length > 0 ? args[0] : null, _group));
      return Task.CompletedTask;
    }
  }

  private sealed class MockClientProxyCapture : IClientProxy {
    private readonly List<(string Method, NotificationMessage? Notification)> _sent;

    public MockClientProxyCapture(List<(string, NotificationMessage?)> sent) {
      _sent = sent;
    }

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) {
      _sent.Add((method, args.Length > 0 ? args[0] as NotificationMessage : null));
      return Task.CompletedTask;
    }
  }
}
