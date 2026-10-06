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
/// Branch backfill for <see cref="ContextPriorityProducerHook"/> (a declaration with no dispatch
/// context at all) and <see cref="PriorityHookChain.IsEmpty"/> (a chain holding hooks of only one
/// kind).
/// </summary>
[Category("Unit")]
public class PriorityHooksBranchCoverageTests {

  private static MessageEnvelope<string> _envelope() => new() {
    MessageId = MessageId.From((Guid)TrackedGuid.New()),
    Payload = "hi",
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Both, Source = MessageSource.Local },
  };

  [Test]
  public async Task ProducerDefault_NoDispatchContext_IsInteractiveAsync() {
    var hook = new ContextPriorityProducerHook();
    var context = new PriorityDeclarationContext(
      _envelope(), "Contracts.SomeCommand, Contracts", DispatchContext: null,
      IsScheduled: false, ParentEffectivePriority: WorkPriority.UNDECLARED, Declared: WorkPriority.UNDECLARED);

    var declared = hook.DeclarePriority(context);

    await Assert.That(declared).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("with no dispatch context there is no handler, so the dispatch is application-initiated");
  }

  [Test]
  public async Task Chain_WithOnlyAProducerHook_IsNotEmptyAsync() {
    var chain = new PriorityHookChain([new Producer()], [], []);
    await Assert.That(chain.IsEmpty).IsFalse();
  }

  [Test]
  public async Task Chain_WithOnlyAReceiveHook_IsNotEmptyAsync() {
    var chain = new PriorityHookChain([], [new Receiver()], []);
    await Assert.That(chain.IsEmpty).IsFalse()
      .Because("a registered receive hook alone is a policy the chain must run");
  }

  [Test]
  public async Task Chain_WithOnlyABatchHook_IsNotEmptyAsync() {
    var chain = new PriorityHookChain([], [], [new Batcher()]);
    await Assert.That(chain.IsEmpty).IsFalse()
      .Because("a registered batch hook alone is a policy the chain must run");
  }

  private sealed class Producer : IPriorityProducerHook {
    public int Order => 100;
    public int DeclarePriority(PriorityDeclarationContext context) => context.Declared;
  }

  private sealed class Receiver : IPriorityReceiveHook {
    public int Order => 100;
    public int Classify(PriorityReceiveContext context) => context.Declared;
  }

  private sealed class Batcher : IPriorityBatchHook {
    public int Order => 100;
    public int Adjust(PriorityBatchEntry stream, IReadOnlyList<PriorityBatchEntry> batch) => stream.FoldedPriority;
  }
}
