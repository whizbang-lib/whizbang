using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;
using Whizbang.SignalR.Hooks;

namespace Whizbang.SignalR.Tests;

/// <summary>
/// Unit tests for <see cref="SignalRNotificationHook{THub}"/>, focused on the group
/// template substitution: a template names payload properties as {Placeholder}, and
/// every JSON value kind has to render into a group name.
/// </summary>
public class SignalRNotificationHookTests {

  private sealed class TestHub : Hub;

  private sealed record Sent(string? Group, string Method, object?[] Args);

  private sealed class CapturingHubContext(List<Sent> sent) : IHubContext<TestHub> {
    public IHubClients Clients { get; } = new CapturingClients(sent);
    public IGroupManager Groups => throw new NotImplementedException();
  }

  private sealed class CapturingClients(List<Sent> sent) : IHubClients {
    public IClientProxy All => new CapturingClientProxy(null, sent);
    public IClientProxy Group(string groupName) => new CapturingClientProxy(groupName, sent);
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
    public IClientProxy Client(string connectionId) => throw new NotImplementedException();
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotImplementedException();
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotImplementedException();
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
    public IClientProxy User(string userId) => throw new NotImplementedException();
    public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotImplementedException();
  }

  private sealed class CapturingClientProxy(string? group, List<Sent> sent) : IClientProxy {
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) {
      sent.Add(new Sent(group, method, args));
      return Task.CompletedTask;
    }
  }

  private sealed record Payload(Guid OrderId, int Attempt, bool Urgent, string Region);

  private static async Task<List<Sent>> _dispatchAsync(string? groupTemplate, string payloadJson) {
    var sent = new List<Sent>();
    var hook = new SignalRNotificationHook<TestHub>(new CapturingHubContext(sent));
    using var doc = JsonDocument.Parse(payloadJson);

    var context = new TagContext<SignalTagAttribute> {
      Attribute = new SignalTagAttribute { Tag = "orders", Group = groupTemplate },
      Message = new object(),
      MessageType = typeof(Payload),
      Payload = doc.RootElement
    };

    await hook.OnTaggedMessageAsync(context, CancellationToken.None);
    return sent;
  }

  [Test]
  public async Task NoGroupTemplate_BroadcastsToAllAsync() {
    var sent = await _dispatchAsync(null, """{"Region":"emea"}""");

    await Assert.That(sent).Count().IsEqualTo(1);
    await Assert.That(sent[0].Group).IsNull();
    await Assert.That(sent[0].Method).IsEqualTo("ReceiveNotification");
  }

  [Test]
  public async Task StringPlaceholder_IsSubstitutedAsync() {
    var sent = await _dispatchAsync("region-{Region}", """{"Region":"emea"}""");

    await Assert.That(sent[0].Group).IsEqualTo("region-emea");
  }

  [Test]
  public async Task NumberPlaceholder_IsSubstitutedAsRawTextAsync() {
    var sent = await _dispatchAsync("attempt-{Attempt}", """{"Attempt":3}""");

    await Assert.That(sent[0].Group).IsEqualTo("attempt-3");
  }

  [Test]
  public async Task TruePlaceholder_IsSubstitutedAsLowercaseAsync() {
    var sent = await _dispatchAsync("urgent-{Urgent}", """{"Urgent":true}""");

    await Assert.That(sent[0].Group).IsEqualTo("urgent-true");
  }

  [Test]
  public async Task FalsePlaceholder_IsSubstitutedAsLowercaseAsync() {
    var sent = await _dispatchAsync("urgent-{Urgent}", """{"Urgent":false}""");

    await Assert.That(sent[0].Group).IsEqualTo("urgent-false");
  }

  [Test]
  public async Task NullPlaceholder_FallsBackToRawTextAsync() {
    // Null, arrays and objects all take the switch's default arm: raw JSON text.
    var sent = await _dispatchAsync("region-{Region}", """{"Region":null}""");

    await Assert.That(sent[0].Group).IsEqualTo("region-null");
  }

  [Test]
  public async Task ArrayPlaceholder_FallsBackToRawTextAsync() {
    var sent = await _dispatchAsync("tags-{Tags}", """{"Tags":[1,2]}""");

    await Assert.That(sent[0].Group).IsEqualTo("tags-[1,2]");
  }

  [Test]
  public async Task SeveralPlaceholders_AreAllSubstitutedAsync() {
    var sent = await _dispatchAsync(
        "{Region}-{Attempt}-{Urgent}",
        """{"Region":"emea","Attempt":7,"Urgent":true}""");

    await Assert.That(sent[0].Group).IsEqualTo("emea-7-true");
  }

  [Test]
  public async Task PlaceholderWithNoMatchingProperty_IsLeftIntactAsync() {
    var sent = await _dispatchAsync("region-{Missing}", """{"Region":"emea"}""");

    await Assert.That(sent[0].Group).IsEqualTo("region-{Missing}");
  }

  [Test]
  public async Task NonObjectPayload_LeavesTheTemplateIntactAsync() {
    var sent = await _dispatchAsync("region-{Region}", "\"just-a-string\"");

    await Assert.That(sent[0].Group).IsEqualTo("region-{Region}");
  }
}
