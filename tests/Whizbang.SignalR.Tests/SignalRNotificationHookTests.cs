// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
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

  private static async Task<List<Sent>> _dispatchAsync(string? groupTemplate, string payloadJson, PerspectiveScope? scope = null) {
    var sent = new List<Sent>();
    var hook = new SignalRNotificationHook<TestHub>(new CapturingHubContext(sent));
    using var doc = JsonDocument.Parse(payloadJson);

    var context = new TagContext<SignalTagAttribute> {
      Attribute = new SignalTagAttribute { Tag = "orders", Group = groupTemplate },
      Message = new object(),
      MessageType = typeof(Payload),
      Payload = doc.RootElement,
      Scope = scope is null ? null : new ScopeContext {
        Scope = scope,
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>()
      }
    };

    await hook.OnTaggedMessageAsync(context, CancellationToken.None);
    return sent;
  }

  private sealed class CapturingLogger : ILogger<SignalRNotificationHook<TestHub>> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }

  [Test]
  [Arguments(null, "has no Group")]
  [Arguments("region-{Missing}", "has a placeholder with no value")]
  public async Task NotificationNotSent_IsLoggedAsAWarning_NamingTheTagAsync(string? groupTemplate, string reason) {
    var logger = new CapturingLogger();
    var sent = new List<Sent>();
    var hook = new SignalRNotificationHook<TestHub>(new CapturingHubContext(sent), logger);
    using var doc = JsonDocument.Parse("""{"Region":"emea"}""");

    await hook.OnTaggedMessageAsync(new TagContext<SignalTagAttribute> {
      Attribute = new SignalTagAttribute { Tag = "orders", Group = groupTemplate },
      Message = new object(),
      MessageType = typeof(Payload),
      Payload = doc.RootElement
    }, CancellationToken.None);

    await Assert.That(sent).IsEmpty();
    await Assert.That(logger.Entries).Count().IsEqualTo(1);
    await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(logger.Entries[0].Message).Contains("orders");
    await Assert.That(logger.Entries[0].Message).Contains(reason);
  }

  [Test]
  public async Task NoGroupTemplate_SendsNothingAsync() {
    // A tag without a group is not a broadcast: reaching every connected client, of every tenant, is opt-in.
    var sent = await _dispatchAsync(null, """{"Region":"emea"}""");

    await Assert.That(sent).IsEmpty();
  }

  [Test]
  public async Task AllGroup_BroadcastsToEveryClientAsync() {
    var sent = await _dispatchAsync("all", """{"Region":"emea"}""");

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
  public async Task PlaceholderWithNoMatchingProperty_SendsNothingAsync() {
    // An unresolved placeholder must not become a literal group every such message shares.
    var sent = await _dispatchAsync("region-{Missing}", """{"Region":"emea"}""");

    await Assert.That(sent).IsEmpty();
  }

  [Test]
  public async Task NonObjectPayload_WithAPlaceholder_SendsNothingAsync() {
    var sent = await _dispatchAsync("region-{Region}", "\"just-a-string\"");

    await Assert.That(sent).IsEmpty();
  }

  [Test]
  public async Task NonObjectPayload_FixedGroup_StillSendsAsync() {
    var sent = await _dispatchAsync("operators", "\"just-a-string\"");

    await Assert.That(sent).Count().IsEqualTo(1);
    await Assert.That(sent[0].Group).IsEqualTo("operators");
  }

  [Test]
  public async Task TenantPlaceholder_ComesFromTheScope_NotAPayloadFieldOfTheSameNameAsync() {
    // A message's payload must never choose which tenant hears about it.
    var sent = await _dispatchAsync("tenant-{TenantId}", """{"TenantId":"payload-tenant"}""", new PerspectiveScope { TenantId = "scope-tenant" });

    await Assert.That(sent).Count().IsEqualTo(1);
    await Assert.That(sent[0].Group).IsEqualTo("tenant-scope-tenant");
  }

  [Test]
  [Arguments("UserId", "user-{UserId}")]
  [Arguments("OrganizationId", "org-{OrganizationId}")]
  [Arguments("CustomerId", "customer-{CustomerId}")]
  public async Task OtherIdentityPlaceholders_PreferThePayload_ThenTheScopeAsync(string field, string template) {
    // Notifying the customer an order belongs to, or the user it was assigned to, is a payload value.
    var scope = new PerspectiveScope { UserId = "scope-u", OrganizationId = "scope-o", CustomerId = "scope-c" };
    var fromPayload = await _dispatchAsync(template, $$"""{"{{field}}":"payload-value"}""", scope);
    var fromScope = await _dispatchAsync(template, """{"Region":"emea"}""", scope);

    await Assert.That(fromPayload[0].Group).EndsWith("payload-value");
    await Assert.That(fromScope[0].Group).Contains("scope-");
  }

  [Test]
  [Arguments("user-{UserId}")]
  [Arguments("org-{OrganizationId}")]
  [Arguments("customer-{CustomerId}")]
  public async Task OtherIdentityPlaceholders_WithNoScope_AndNoPayloadValue_SendNothingAsync(string template) {
    // Neither the payload nor a scope supplies the value, so there is no group to send to.
    var sent = await _dispatchAsync(template, """{"Region":"emea"}""", scope: null);

    await Assert.That(sent).IsEmpty();
  }

  [Test]
  public async Task TenantPlaceholder_WithNoScopeValue_SendsNothing_EvenIfThePayloadHasOneAsync() {
    var sent = await _dispatchAsync("tenant-{TenantId}", """{"TenantId":"payload-tenant"}""", new PerspectiveScope());

    await Assert.That(sent).IsEmpty();
  }
}
