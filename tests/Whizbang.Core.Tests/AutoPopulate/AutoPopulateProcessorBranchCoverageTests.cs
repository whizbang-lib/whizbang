// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.AutoPopulate;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.AutoPopulate;

/// <summary>
/// Branch coverage for <see cref="AutoPopulateProcessor"/>'s context extraction: the hop's scope
/// element can lack the user/tenant key entirely, or carry it as an explicit JSON null. Both must
/// populate nothing rather than throw or stamp a fabricated identity.
/// </summary>
/// <code-under-test>src/Whizbang.Core/AutoPopulate/AutoPopulateProcessor.cs</code-under-test>
[Category("Core")]
[Category("AutoPopulate")]
public class AutoPopulateProcessorBranchCoverageTests {

  private sealed class Registry(AutoPopulateRegistration registration) : IAutoPopulateRegistry {
    public IEnumerable<AutoPopulateRegistration> GetRegistrationsFor(Type messageType) =>
      registration.MessageType == messageType ? [registration] : [];
    public IEnumerable<AutoPopulateRegistration> GetAllRegistrations() => [registration];
  }

  // One private message type per scenario: the process-global registry keys by exact type, so no
  // other test can contribute a registration these scenarios would pick up.
  private sealed record MissingUserKeyMessage(Guid Id);
  private sealed record NullUserKeyMessage(Guid Id);
  private sealed record MissingTenantKeyMessage(Guid Id);
  private sealed record NullTenantKeyMessage(Guid Id);

  private static ScopeDelta _scopeFromJson(string scopeJson) {
    using var document = JsonDocument.Parse(scopeJson);
    return new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Scope] = document.RootElement.Clone()
      }
    };
  }

  private static MessageEnvelope<TMessage> _createEnvelope<TMessage>(TMessage payload, ScopeDelta scope) =>
    new() {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "localhost",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          Scope = scope
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

  private static AutoPopulateRegistration _contextRegistration(Type messageType, string propertyName, ContextKind kind) =>
    new() {
      MessageType = messageType,
      PropertyName = propertyName,
      PropertyType = typeof(string),
      PopulateKind = PopulateKind.Context,
      ContextKind = kind
    };

  [Test]
  public async Task ProcessAutoPopulate_UserIdKeyAbsentFromScope_PopulatesNothingAsync() {
    AutoPopulateRegistry.Register(
      new Registry(_contextRegistration(typeof(MissingUserKeyMessage), "UserFromScope", ContextKind.UserId)), priority: 9211);
    // The scope carries a tenant but no "u" key at all.
    var envelope = _createEnvelope(new MissingUserKeyMessage(Guid.NewGuid()), _scopeFromJson("""{"t":"tenant-1"}"""));
    var initialHopCount = envelope.Hops.Count;

    new AutoPopulateProcessor().ProcessAutoPopulate(envelope, typeof(MissingUserKeyMessage));

    await Assert.That(envelope.GetMetadata("auto:UserFromScope")).IsNull()
      .Because("a scope with no user key has no user to stamp; the tenant must never be read in its place");
    await Assert.That(envelope.Hops.Count).IsEqualTo(initialHopCount)
      .Because("nothing was extracted, so no auto-populate hop may be appended");
  }

  [Test]
  public async Task ProcessAutoPopulate_UserIdKeyIsJsonNull_PopulatesNothingAsync() {
    AutoPopulateRegistry.Register(
      new Registry(_contextRegistration(typeof(NullUserKeyMessage), "UserFromScope", ContextKind.UserId)), priority: 9212);
    var envelope = _createEnvelope(new NullUserKeyMessage(Guid.NewGuid()), _scopeFromJson("""{"t":"tenant-1","u":null}"""));

    new AutoPopulateProcessor().ProcessAutoPopulate(envelope, typeof(NullUserKeyMessage));

    await Assert.That(envelope.GetMetadata("auto:UserFromScope")).IsNull()
      .Because("an explicit null user is 'no user', not the string \"null\" or an empty identity");
  }

  [Test]
  public async Task ProcessAutoPopulate_TenantIdKeyAbsentFromScope_PopulatesNothingAsync() {
    AutoPopulateRegistry.Register(
      new Registry(_contextRegistration(typeof(MissingTenantKeyMessage), "TenantFromScope", ContextKind.TenantId)), priority: 9213);
    var envelope = _createEnvelope(new MissingTenantKeyMessage(Guid.NewGuid()), _scopeFromJson("""{"u":"user-1"}"""));
    var initialHopCount = envelope.Hops.Count;

    new AutoPopulateProcessor().ProcessAutoPopulate(envelope, typeof(MissingTenantKeyMessage));

    await Assert.That(envelope.GetMetadata("auto:TenantFromScope")).IsNull()
      .Because("a scope with no tenant key has no tenant to stamp; the user must never be read in its place");
    await Assert.That(envelope.Hops.Count).IsEqualTo(initialHopCount);
  }

  [Test]
  public async Task ProcessAutoPopulate_TenantIdKeyIsJsonNull_PopulatesNothingAsync() {
    AutoPopulateRegistry.Register(
      new Registry(_contextRegistration(typeof(NullTenantKeyMessage), "TenantFromScope", ContextKind.TenantId)), priority: 9214);
    var envelope = _createEnvelope(new NullTenantKeyMessage(Guid.NewGuid()), _scopeFromJson("""{"t":null,"u":"user-1"}"""));

    new AutoPopulateProcessor().ProcessAutoPopulate(envelope, typeof(NullTenantKeyMessage));

    await Assert.That(envelope.GetMetadata("auto:TenantFromScope")).IsNull()
      .Because("an explicit null tenant is 'no tenant', not a tenant literally named null");
  }
}
