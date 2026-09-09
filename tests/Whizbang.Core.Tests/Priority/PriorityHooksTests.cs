using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
/// The three priority hooks and their framework defaults. The producer hook declares from dispatch context
/// (an application-initiated dispatch is interactive, scheduled work is background, everything else is
/// standard, and a child inherits its parent's effective number); the receive hook accepts the declared
/// number and reads an undeclared one as standard; the chain runs hooks in order and each sees the previous
/// answer. Every policy the framework ships is one of these hooks, so a developer's own replaces it.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#hooks</docs>
[Category("Unit")]
public class PriorityHooksTests {

  private static MessageEnvelope<string> _envelope(MessageSource source, string? handlerName = null, int declared = WorkPriority.UNDECLARED) => new() {
    MessageId = MessageId.From((Guid)TrackedGuid.NewMedo()),
    Payload = "hi",
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Both, Source = source, HandlerName = handlerName },
    Priority = declared,
  };

  private static PriorityDeclarationContext _declaration(MessageEnvelope<string> envelope, bool scheduled = false, int parent = WorkPriority.UNDECLARED, int declared = WorkPriority.UNDECLARED) =>
    new(envelope, "Contracts.SomeCommand, Contracts", envelope.DispatchContext, scheduled, parent, declared);

  // ---- producer default ----------------------------------------------------------------------------

  [Test]
  public async Task ProducerDefault_AnApplicationInitiatedDispatch_IsInteractiveAsync() {
    var hook = new ContextPriorityProducerHook();
    var declared = hook.DeclarePriority(_declaration(_envelope(MessageSource.Local)));
    await Assert.That(declared).IsEqualTo(WorkPriority.INTERACTIVE)
      .Because("a message the application dispatches outside any handler is a caller waiting at a boundary");
  }

  [Test]
  public async Task ProducerDefault_ADispatchInsideAHandler_IsStandardAsync() {
    var hook = new ContextPriorityProducerHook();
    var declared = hook.DeclarePriority(_declaration(_envelope(MessageSource.Local, handlerName: "OrderHandler")));
    await Assert.That(declared).IsEqualTo(WorkPriority.STANDARD)
      .Because("work a handler emits is domain work with no one waiting, unless its parent says otherwise");
  }

  [Test]
  public async Task ProducerDefault_ScheduledWork_IsBackgroundAsync() {
    var hook = new ContextPriorityProducerHook();
    var declared = hook.DeclarePriority(_declaration(_envelope(MessageSource.Local), scheduled: true));
    await Assert.That(declared).IsEqualTo(WorkPriority.BACKGROUND);
  }

  [Test]
  public async Task ProducerDefault_AChildInheritsItsParentsEffectiveNumberAsync() {
    var hook = new ContextPriorityProducerHook();
    var declared = hook.DeclarePriority(_declaration(_envelope(MessageSource.Local, handlerName: "ImportItemHandler"), parent: WorkPriority.BACKGROUND));
    await Assert.That(declared).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("a message produced while handling background work is background, whatever its type normally is");
  }

  [Test]
  public async Task ProducerDefault_InheritanceNeverRaisesAboveTheParentAsync() {
    var hook = new ContextPriorityProducerHook();
    // The application-initiated rule would say Interactive; the parent is Background.
    var declared = hook.DeclarePriority(_declaration(_envelope(MessageSource.Local), parent: WorkPriority.BACKGROUND));
    await Assert.That(declared).IsEqualTo(WorkPriority.BACKGROUND)
      .Because("a child can never be more urgent than its parent by inheritance alone; only an explicit declaration raises it");
  }

  [Test]
  public async Task ProducerDefault_KeepsAnExplicitDeclarationAsync() {
    var hook = new ContextPriorityProducerHook();
    var declared = hook.DeclarePriority(_declaration(_envelope(MessageSource.Local, handlerName: "H"), parent: WorkPriority.BACKGROUND, declared: 20));
    await Assert.That(declared).IsEqualTo(20)
      .Because("an explicit declaration is recorded and wins over the derived default; raising above the parent is allowed only this way");
  }

  // ---- receive default -----------------------------------------------------------------------------

  [Test]
  public async Task ReceiveDefault_AcceptsTheDeclaredNumber_AndReadsUndeclaredAsStandardAsync() {
    var hook = new AcceptDeclaredPriorityReceiveHook();
    var env = _envelope(MessageSource.Outbox);
    await Assert.That(hook.Classify(new PriorityReceiveContext(30, env, "Contracts.X, Contracts"))).IsEqualTo(30);
    await Assert.That(hook.Classify(new PriorityReceiveContext(WorkPriority.UNDECLARED, env, "Contracts.X, Contracts"))).IsEqualTo(WorkPriority.STANDARD)
      .Because("a miss lands in the middle band; nothing undeclared can occupy the urgent bucket");
  }

  // ---- chain ---------------------------------------------------------------------------------------

  private sealed class _producer(int order, Func<PriorityDeclarationContext, int> f) : IPriorityProducerHook {
    public int Order => order;
    public int DeclarePriority(PriorityDeclarationContext context) => f(context);
  }

  private sealed class _receiver(int order, Func<PriorityReceiveContext, int> f) : IPriorityReceiveHook {
    public int Order => order;
    public int Classify(PriorityReceiveContext context) => f(context);
  }

  [Test]
  public async Task Chain_RunsProducerHooksInOrder_EachSeeingThePreviousAnswerAsync() {
    var chain = new PriorityHookChain(
      [new _producer(200, c => c.Declared + 1), new _producer(100, _ => 10)],
      [], []);

    var declared = chain.DeclarePriority(_declaration(_envelope(MessageSource.Local)));

    await Assert.That(declared).IsEqualTo(11)
      .Because("order 100 declared 10, order 200 saw 10 and added one; the sort is by Order, not registration");
  }

  [Test]
  public async Task Chain_RunsReceiveHooksInOrder_AndTheLastWordWinsAsync() {
    var chain = new PriorityHookChain(
      [],
      [new _receiver(500, c => c.Declared * 2), new _receiver(100, _ => 40)],
      []);

    var effective = chain.Classify(new PriorityReceiveContext(150, _envelope(MessageSource.Outbox), "Contracts.X, Contracts"));

    await Assert.That(effective).IsEqualTo(80).Because("the consumer's rules compose; the later hook sees what the earlier one decided");
  }

  [Test]
  public async Task Chain_WithNoHooks_ReturnsWhatItWasGivenAsync() {
    var chain = new PriorityHookChain([], [], []);
    await Assert.That(chain.DeclarePriority(_declaration(_envelope(MessageSource.Local), declared: 7))).IsEqualTo(7);
    await Assert.That(chain.Classify(new PriorityReceiveContext(7, _envelope(MessageSource.Outbox), "X"))).IsEqualTo(7);
    await Assert.That(chain.IsEmpty).IsTrue();
  }

  [Test]
  public async Task Defaults_AreRegisteredByTheCoreRegistration_SoAHostGetsThemWithoutWiringAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangPriority();
    using var sp = services.BuildServiceProvider();

    var chain = sp.GetRequiredService<PriorityHookChain>();
    var declared = chain.DeclarePriority(_declaration(_envelope(MessageSource.Local)));
    var effective = chain.Classify(new PriorityReceiveContext(WorkPriority.UNDECLARED, _envelope(MessageSource.Outbox), "X"));

    await Assert.That(declared).IsEqualTo(WorkPriority.INTERACTIVE).Because("the context-derived producer default is registered");
    await Assert.That(effective).IsEqualTo(WorkPriority.STANDARD).Because("the accept-declared receive default is registered");
    await Assert.That(sp.GetServices<IPriorityProducerHook>().Count()).IsEqualTo(1)
      .Because("registered with TryAddEnumerable, so a second registration of the same default is a no-op and a host's own hook adds to the chain");
  }

  // ---- ambient parent ------------------------------------------------------------------------------

  [Test]
  public async Task PriorityContext_FlowsTheParentsEffectiveNumberToWorkStartedWhileHandlingAsync() {
    await Assert.That(PriorityContext.CurrentParent).IsEqualTo(WorkPriority.UNDECLARED);
    int seenInside;
    int seenInChild;
    using (PriorityContext.Enter(WorkPriority.BACKGROUND)) {
      seenInside = PriorityContext.CurrentParent;
      seenInChild = await Task.Run(() => PriorityContext.CurrentParent);
    }

    await Assert.That(seenInside).IsEqualTo(WorkPriority.BACKGROUND);
    await Assert.That(seenInChild).IsEqualTo(WorkPriority.BACKGROUND).Because("the ambient value must follow the async flow into the work a handler starts");
    await Assert.That(PriorityContext.CurrentParent).IsEqualTo(WorkPriority.UNDECLARED).Because("leaving the handling clears it");
  }

  // ---- envelope wire format --------------------------------------------------------------------------

  [Test]
  public async Task Envelope_CarriesTheDeclaredPriorityUnderAShortKey_AndOmitsItWhenUndeclaredAsync() {
    var declared = _envelope(MessageSource.Local, declared: 42);
    var undeclared = _envelope(MessageSource.Local);

    var declaredJson = JsonSerializer.Serialize(declared);
    var undeclaredJson = JsonSerializer.Serialize(undeclared);
    var roundtripped = JsonSerializer.Deserialize<MessageEnvelope<string>>(declaredJson)!;

    await Assert.That(declaredJson).Contains("\"pri\":42").Because("one small field on the wire, like sto and sid");
    await Assert.That(undeclaredJson).DoesNotContain("\"pri\"").Because("an undeclared priority costs nothing on the wire");
    await Assert.That(roundtripped.Priority).IsEqualTo(42);
    await Assert.That(JsonSerializer.Deserialize<MessageEnvelope<string>>(undeclaredJson)!.Priority).IsEqualTo(WorkPriority.UNDECLARED);
  }
}
