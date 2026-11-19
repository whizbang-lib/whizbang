// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Priority;

/// <summary>
/// A type rule is keyed by the canonical wire form (<c>Namespace.Type, Assembly</c>) and matched against a received
/// name in that form (#2267). The rules were keyed by the local type's versioned assembly-qualified name and matched
/// against the received envelope's message type, which carries the publisher's: a publisher on another build of the
/// contract assembly made every type rule miss, silently.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Priority/PriorityOptions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Priority/PrioritySugarHooks.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Priority/IdleBandOptions.cs</code-under-test>
/// <docs>fundamentals/messaging/message-priority#declaring-with-tags</docs>
public class PriorityCrossBuildTypeNameTests {
  private sealed record Revoked(string User) : IEvent;
  private sealed record Unrelated(string Id) : IEvent;

  private static string _asAnotherBuildWritesIt(Type type) =>
    $"{TypeNameFormatter.Format(type)}, Version=9.8.7.6, Culture=neutral, PublicKeyToken=0123456789abcdef";

  private static PriorityReceiveContext _receive(string messageTypeName, int declared) =>
    new(declared, new MessageEnvelope<string> {
      MessageId = MessageId.From((Guid)TrackedGuid.New()),
      Payload = "x",
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Both, Source = MessageSource.Local, HandlerName = "H" },
      Priority = declared,
    }, messageTypeName);

  [Test]
  public async Task ClassifyType_MatchesAMessageWhosePublisherRanAnotherBuildAsync() {
    var hook = new PriorityClassificationReceiveHook(new PriorityOptions().ClassifyType<Revoked>(WorkPriority.INTERACTIVE));

    await Assert.That(hook.Classify(_receive(_asAnotherBuildWritesIt(typeof(Revoked)), WorkPriority.STANDARD)))
      .IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("the publisher's assembly version says nothing about which type the message is");
    await Assert.That(hook.Classify(_receive(TypeNameFormatter.Format(typeof(Revoked)), WorkPriority.STANDARD)))
      .IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("the version-free wire form matches too");
    await Assert.That(hook.Classify(_receive(_asAnotherBuildWritesIt(typeof(Unrelated)), WorkPriority.STANDARD)))
      .IsEqualTo(WorkPriority.STANDARD);
  }

  [Test]
  public async Task ClassifyType_KeysTheRuleByTheCanonicalWireFormAsync() {
    var options = new PriorityOptions().ClassifyType<Revoked>(WorkPriority.INTERACTIVE);

    await Assert.That(options.TypeRules.Keys.Single()).IsEqualTo(TypeNameFormatter.Format(typeof(Revoked)));
  }

  [Test]
  public async Task TrickleType_MatchesAStoredNameFromAnotherBuild_AndIsKeyedByTheWireFormAsync() {
    var options = new IdleBandOptions().TrickleType<Revoked>();

    await Assert.That(options.TrickleTypes.Single()).IsEqualTo(TypeNameFormatter.Format(typeof(Revoked)));
    await Assert.That(options.Trickles(_asAnotherBuildWritesIt(typeof(Revoked)))).IsTrue();
    await Assert.That(options.Trickles(_asAnotherBuildWritesIt(typeof(Unrelated)))).IsFalse();
  }
}
