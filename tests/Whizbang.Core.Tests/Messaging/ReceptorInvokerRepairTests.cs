// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2273: a repaired event this service can prove it never handled runs only the receptors declared
/// <see cref="RunOnRepairAttribute"/>. Every other receptor stays state only for it, and an ordinary delivery runs them
/// all as before.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
/// <docs>resilience/stream-integrity#repaired-events-and-receptors</docs>
public class ReceptorInvokerRepairTests {
  private sealed record RepairedThing(string Value) : IEvent;

  private sealed class Registry : IReceptorRegistry {
    private readonly List<ReceptorInfo> _receptors = [];
    public List<string> Ran { get; } = [];

    public void Add(string id, bool runOnRepair) =>
      _receptors.Add(new ReceptorInfo(
        MessageType: typeof(RepairedThing),
        ReceptorId: id,
        InvokeAsync: (_, _, _, _, _) => {
          Ran.Add(id);
          return ValueTask.FromResult<object?>(null);
        },
        RunOnRepair: runOnRepair));

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) =>
      messageType == typeof(RepairedThing) && stage == LifecycleStage.PostInboxInline ? _receptors : [];

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => throw new NotSupportedException();
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => throw new NotSupportedException();
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => throw new NotSupportedException();
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => throw new NotSupportedException();
  }

  private static MessageEnvelope<RepairedThing> _envelope() => new() {
    MessageId = MessageId.From(TrackedGuid.New()),
    Payload = new RepairedThing("x"),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
  };

  private static LifecycleExecutionContext _context(bool repair) => new() {
    CurrentStage = LifecycleStage.PostInboxInline,
    MessageSource = MessageSource.Inbox,
    IsRepair = repair,
  };

  [Test]
  public async Task ARepairedEvent_RunsOnlyTheReceptorsDeclaredToRunOnRepairAsync() {
    var registry = new Registry();
    registry.Add("Reacts", runOnRepair: false);
    registry.Add("Converges", runOnRepair: true);
    var invoker = new ReceptorInvoker(registry, new ServiceCollection().BuildServiceProvider());

    await invoker.InvokeAsync(_envelope(), LifecycleStage.PostInboxInline, _context(repair: true));

    await Assert.That(registry.Ran).IsEquivalentTo(["Converges"])
      .Because("a receptor that did not opt in is state only for a repaired event, whatever the ledger proves");
  }

  [Test]
  public async Task ARepairedEvent_WithNoReceptorDeclaredToRunOnRepair_RunsNothingAsync() {
    var registry = new Registry();
    registry.Add("Reacts", runOnRepair: false);
    var invoker = new ReceptorInvoker(registry, new ServiceCollection().BuildServiceProvider());

    await invoker.InvokeAsync(_envelope(), LifecycleStage.PostInboxInline, _context(repair: true));

    await Assert.That(registry.Ran).IsEmpty();
  }

  /// <summary>The control for the two tests above: the same receptors all run for an ordinary delivery.</summary>
  [Test]
  public async Task AnOrdinaryDelivery_RunsEveryReceptorAsync() {
    var registry = new Registry();
    registry.Add("Reacts", runOnRepair: false);
    registry.Add("Converges", runOnRepair: true);
    var invoker = new ReceptorInvoker(registry, new ServiceCollection().BuildServiceProvider());

    await invoker.InvokeAsync(_envelope(), LifecycleStage.PostInboxInline, _context(repair: false));

    await Assert.That(registry.Ran).IsEquivalentTo(["Reacts", "Converges"]);
  }

  [Test]
  public async Task TheLifecycleContext_IsNotARepair_UnlessSaidAsync() {
    var context = new LifecycleExecutionContext { CurrentStage = LifecycleStage.PostInboxInline };
    ILifecycleContext minimal = new MinimalContext();

    await Assert.That(context.IsRepair).IsFalse();
    await Assert.That(minimal.IsRepair).IsFalse()
      .Because("a context that does not say is an ordinary delivery, so every receptor runs as before");
  }

  private sealed class MinimalContext : ILifecycleContext {
    public LifecycleStage CurrentStage => LifecycleStage.PostInboxInline;
    public Guid? EventId => null;
    public Guid? StreamId => null;
    public Type? PerspectiveType => null;
    public Guid? LastProcessedEventId => null;
    public MessageSource? MessageSource => null;
    public int? AttemptNumber => null;
    public ProcessingMode? ProcessingMode => null;
  }
}
