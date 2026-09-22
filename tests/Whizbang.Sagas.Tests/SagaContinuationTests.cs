using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core;
using Whizbang.Sagas;
using Whizbang.Sagas.Helpers;
using Whizbang.Sagas.Models;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// Running one saga after another finishes, instead of alongside it.
/// </summary>
/// <remarks>
/// <para>
/// Two workloads belonging to one operation contend for claim capacity when nothing sequences them.
/// Declaring the second one less urgent does not sequence it: the claim orders by arrival inside a
/// bucket, holds a floor for background work, and promotes background work that has waited. So the
/// second workload keeps taking capacity from the first and gets more urgent the longer the first
/// runs. What removes the contention is not queuing it yet.
/// </para>
/// <para>
/// The continuation's own claim key is the part worth pinning. Publishing it only when a caller wins
/// the completion claim loses it permanently if that process dies in between, because the completion
/// claim is then taken and nothing will drive it again. A separate key lets every terminal caller
/// attempt it while still collapsing them to one emission.
/// </para>
/// </remarks>
/// <docs>fundamentals/sagas/continuations</docs>
[NotInParallel("SagaContinuationRegistry is static")]
public class SagaContinuationTests {
  private const string PARENT = "ContinuationTestsParent";
  private const string CHILD = "ContinuationTestsChild";

  /// <summary>A saga nobody chained has no continuations.</summary>
  [Test]
  public async Task ASagaWithNoDeclarationHasNoContinuationsAsync() {
    await Assert.That(SagaContinuationRegistry.For("ContinuationTestsUndeclared")).IsEmpty();
  }

  /// <summary>What was declared is what comes back.</summary>
  [Test]
  public async Task ADeclaredContinuationIsReturnedAsync() {
    SagaContinuationRegistry.Register(
      "ContinuationTestsReturned", new SagaContinuation(CHILD, SagaContinuationTriggers.RanToTheEnd));

    var continuations = SagaContinuationRegistry.For("ContinuationTestsReturned");

    await Assert.That(continuations.Count).IsEqualTo(1);
    await Assert.That(continuations[0].SagaName).IsEqualTo(CHILD);
    await Assert.That(continuations[0].Trigger).IsEqualTo(SagaContinuationTriggers.RanToTheEnd);
  }

  /// <summary>
  /// A saga can be followed by several, and one declared twice is recorded once.
  /// </summary>
  /// <remarks>
  /// Assemblies can register the same declaration more than once (a library and the host that
  /// composes it), and a chain that fired twice would do the downstream work twice.
  /// </remarks>
  [Test]
  public async Task SeveralContinuationsAreKeptAndDuplicatesAreNotAsync() {
    const string PARENT_NAME = "ContinuationTestsSeveral";
    SagaContinuationRegistry.Register(PARENT_NAME, new SagaContinuation("ChildA"));
    SagaContinuationRegistry.Register(PARENT_NAME, new SagaContinuation("ChildB"));
    SagaContinuationRegistry.Register(PARENT_NAME, new SagaContinuation("ChildA"));

    var names = SagaContinuationRegistry.For(PARENT_NAME).Select(c => c.SagaName).ToList();

    await Assert.That(names.Count).IsEqualTo(2);
    await Assert.That(names).Contains("ChildA");
    await Assert.That(names).Contains("ChildB");
  }

  /// <summary>The default trigger is "ran to the end", not "succeeded outright".</summary>
  /// <remarks>
  /// A partially failed run still produced rows the follow-on work applies to. An aborted one did
  /// not, which is why <see cref="SagaStatus.Failed"/> is excluded from the default.
  /// </remarks>
  [Test]
  public async Task TheDefaultTriggerIsRanToTheEndAsync() {
    var continuation = new SagaContinuation(CHILD);

    await Assert.That(continuation.Trigger).IsEqualTo(SagaContinuationTriggers.RanToTheEnd);
    await Assert.That(continuation.StartsAfter(SagaStatus.Completed)).IsTrue();
    await Assert.That(continuation.StartsAfter(SagaStatus.CompletedWithFailures)).IsTrue()
      .Because("a partial run still produced work for the follow-on to do");
    await Assert.That(continuation.StartsAfter(SagaStatus.Failed)).IsFalse()
      .Because("an aborted saga has not produced the state the continuation assumes");
  }

  /// <summary>A narrower trigger excludes what it does not name.</summary>
  [Test]
  public async Task ANarrowTriggerOnlyStartsOnWhatItNamesAsync() {
    var continuation = new SagaContinuation(CHILD, SagaContinuationTriggers.Completed);

    await Assert.That(continuation.StartsAfter(SagaStatus.Completed)).IsTrue();
    await Assert.That(continuation.StartsAfter(SagaStatus.CompletedWithFailures)).IsFalse();
  }

  /// <summary>A trigger can name the aborted case, for cleanup that only runs on failure.</summary>
  [Test]
  public async Task AFailureTriggerStartsOnAbortAsync() {
    var continuation = new SagaContinuation(CHILD, SagaContinuationTriggers.Failed);

    await Assert.That(continuation.StartsAfter(SagaStatus.Failed)).IsTrue();
    await Assert.That(continuation.StartsAfter(SagaStatus.Completed)).IsFalse();
  }

  /// <summary>A non-terminal status never starts a continuation.</summary>
  /// <remarks>
  /// <see cref="SagaStatus.Reset"/> is a transition marker and <see cref="SagaStatus.Running"/> means
  /// the saga is still going. Starting a follow-on from either would run it against a half-written set.
  /// </remarks>
  [Test]
  [Arguments(SagaStatus.Pending)]
  [Arguments(SagaStatus.Running)]
  [Arguments(SagaStatus.Reset)]
  public async Task ANonTerminalStatusNeverStartsAContinuationAsync(SagaStatus status) {
    var everything = new SagaContinuation(
      CHILD,
      SagaContinuationTriggers.Completed
        | SagaContinuationTriggers.CompletedWithFailures
        | SagaContinuationTriggers.Failed);

    await Assert.That(everything.StartsAfter(status)).IsFalse();
  }

  /// <summary>A blank name is a declaration error, caught where it is made.</summary>
  [Test]
  public async Task ABlankNameIsRejectedAsync() {
    await Assert.That(() => new SagaContinuation("")).Throws<ArgumentException>();
    await Assert.That(() => new SagaContinuation("   ")).Throws<ArgumentException>();
    await Assert.That(() => SagaContinuationRegistry.Register("", new SagaContinuation(CHILD)))
      .Throws<ArgumentException>();
    await Assert.That(() => SagaContinuationRegistry.Register(PARENT, null!))
      .Throws<ArgumentNullException>();
    await Assert.That(() => SagaContinuationRegistry.For("")).Throws<ArgumentException>();
  }

  /// <summary>
  /// The attribute carries what was written on it, including its default.
  /// </summary>
  /// <remarks>
  /// The generator reads this through Roslyn symbols, so the build path never constructs it. The
  /// default is worth pinning anyway: it is what every <c>[ContinuesWith("X")]</c> with no trigger
  /// means, and changing it would silently rewire every such declaration at once.
  /// </remarks>
  [Test]
  public async Task TheAttributeCarriesWhatWasWrittenAsync() {
    var declared = new ContinuesWithAttribute(CHILD);

    await Assert.That(declared.SagaName).IsEqualTo(CHILD);
    await Assert.That(declared.Trigger).IsEqualTo(SagaContinuationTriggers.RanToTheEnd)
      .Because("a partially failed run still produced state the follow-on applies to");

    var narrowed = new ContinuesWithAttribute("Cleanup", SagaContinuationTriggers.Failed);

    await Assert.That(narrowed.SagaName).IsEqualTo("Cleanup");
    await Assert.That(narrowed.Trigger).IsEqualTo(SagaContinuationTriggers.Failed);
  }

  /// <summary>
  /// The continuation's claim key is distinct from the completion's, and from another child's.
  /// </summary>
  /// <remarks>
  /// Sharing the completion key would mean the continuation is only ever attempted by the caller that
  /// won completion, and lost for good if that process died before publishing. Sharing one key across
  /// children would start only the first of them.
  /// </remarks>
  [Test]
  public async Task TheContinuationClaimKeyIsItsOwnAsync() {
    var sagaId = Guid.NewGuid();

    var key = SagaContinuationGuard.ClaimKey(PARENT, sagaId, CHILD);

    await Assert.That(key).IsNotEqualTo(SagaCompletionGuard.ClaimKey(PARENT, sagaId))
      .Because("a continuation that shared the completion claim would be lost if the winner died "
        + "before publishing it");
    await Assert.That(key).IsNotEqualTo(SagaContinuationGuard.ClaimKey(PARENT, sagaId, "OtherChild"));
    await Assert.That(key).IsEqualTo($"saga-continuation:{PARENT}:{sagaId}:{CHILD}");
  }

  /// <summary>Reaching the end of a chained saga asks for the next one.</summary>
  [Test]
  public async Task CompletingAChainedSagaRequestsTheContinuationAsync() {
    const string PARENT_NAME = "ContinuationTestsRequests";
    SagaContinuationRegistry.Register(PARENT_NAME, new SagaContinuation(CHILD));

    var emitter = new RecordingEmitter();
    var service = new ChainTestSagaService(PARENT_NAME, emitter);
    var ctx = new SagaContext(Guid.NewGuid(), Guid.NewGuid());

    await service.CompleteSagaAsync(ctx, SagaStatus.Completed, "item-1", 1, 0, 1, CancellationToken.None);

    var requested = emitter.PublishedOnce
      .Select(p => p.Event)
      .OfType<SagaContinuationRequestedEvent>()
      .ToList();

    await Assert.That(requested.Count).IsEqualTo(1);
    await Assert.That(requested[0].SagaName).IsEqualTo(CHILD)
      .Because("the event names the saga being asked to start, so a receptor filters on the name "
        + "it declared");
    await Assert.That(requested[0].ParentSagaName).IsEqualTo(PARENT_NAME);
    await Assert.That(requested[0].ParentSagaId).IsEqualTo(ctx.SagaId);
    await Assert.That(requested[0].ParentFinalStatus).IsEqualTo(SagaStatus.Completed);
    await Assert.That(requested[0].EntityId).IsEqualTo(ctx.EntityId)
      .Because("the continuation acts on the same domain entity the parent did");
  }

  /// <summary>An unchained saga completes exactly as it did before.</summary>
  [Test]
  public async Task AnUnchainedSagaRequestsNothingAsync() {
    var emitter = new RecordingEmitter();
    var service = new ChainTestSagaService("ContinuationTestsUnchained", emitter);

    await service.CompleteSagaAsync(
      new SagaContext(Guid.NewGuid(), Guid.NewGuid()),
      SagaStatus.Completed, "item-1", 1, 0, 1, CancellationToken.None);

    await Assert.That(emitter.PublishedOnce.Select(p => p.Event)
      .OfType<SagaContinuationRequestedEvent>()).IsEmpty();
    await Assert.That(emitter.PublishedOnce.Count).IsEqualTo(1)
      .Because("the completion event itself is still published");
  }

  /// <summary>A final status the trigger does not name asks for nothing.</summary>
  [Test]
  public async Task AStatusTheTriggerExcludesRequestsNothingAsync() {
    const string PARENT_NAME = "ContinuationTestsExcluded";
    SagaContinuationRegistry.Register(
      PARENT_NAME, new SagaContinuation(CHILD, SagaContinuationTriggers.Completed));

    var emitter = new RecordingEmitter();
    var service = new ChainTestSagaService(PARENT_NAME, emitter);

    await service.CompleteSagaAsync(
      new SagaContext(Guid.NewGuid(), Guid.NewGuid()),
      SagaStatus.CompletedWithFailures, "item-1", 0, 1, 1, CancellationToken.None);

    await Assert.That(emitter.PublishedOnce.Select(p => p.Event)
      .OfType<SagaContinuationRequestedEvent>()).IsEmpty();
  }

  /// <summary>
  /// A caller that loses the completion claim still asks for the continuation.
  /// </summary>
  /// <remarks>
  /// This is the loss window the separate claim key exists to close. If only the completion winner
  /// attempted the continuation, a process dying between the two publishes would strand the chain
  /// forever, because the completion claim is already taken and no retry or watchdog tick re-emits it.
  /// </remarks>
  [Test]
  public async Task LosingTheCompletionClaimStillRequestsTheContinuationAsync() {
    const string PARENT_NAME = "ContinuationTestsLostClaim";
    SagaContinuationRegistry.Register(PARENT_NAME, new SagaContinuation(CHILD));

    var emitter = new RecordingEmitter { WinClaims = false };
    var service = new ChainTestSagaService(PARENT_NAME, emitter);

    var won = await service.CompleteSagaAsync(
      new SagaContext(Guid.NewGuid(), Guid.NewGuid()),
      SagaStatus.Completed, "item-1", 1, 0, 1, CancellationToken.None);

    await Assert.That(won).IsFalse();
    await Assert.That(emitter.PublishedOnce.Select(p => p.Event)
      .OfType<SagaContinuationRequestedEvent>().Count()).IsEqualTo(1)
      .Because("the continuation's own claim dedups it, so every terminal caller may attempt it and "
        + "none of them is the single point of failure");
  }

  /// <summary>Every continuation of a saga is asked for, under its own claim.</summary>
  [Test]
  public async Task EveryContinuationIsRequestedUnderItsOwnClaimAsync() {
    const string PARENT_NAME = "ContinuationTestsAll";
    SagaContinuationRegistry.Register(PARENT_NAME, new SagaContinuation("ChildOne"));
    SagaContinuationRegistry.Register(PARENT_NAME, new SagaContinuation("ChildTwo"));

    var emitter = new RecordingEmitter();
    var service = new ChainTestSagaService(PARENT_NAME, emitter);
    var ctx = new SagaContext(Guid.NewGuid(), Guid.NewGuid());

    await service.CompleteSagaAsync(ctx, SagaStatus.Completed, "item-1", 1, 0, 1, CancellationToken.None);

    var keys = emitter.PublishedOnce
      .Where(p => p.Event is SagaContinuationRequestedEvent)
      .Select(p => p.ClaimKey)
      .ToList();

    await Assert.That(keys).Contains(SagaContinuationGuard.ClaimKey(PARENT_NAME, ctx.SagaId, "ChildOne"));
    await Assert.That(keys).Contains(SagaContinuationGuard.ClaimKey(PARENT_NAME, ctx.SagaId, "ChildTwo"));
  }

  /// <summary>
  /// A continuation that fails to publish does not take the completion down with it.
  /// </summary>
  /// <remarks>
  /// The saga did finish, and its completion event is the durable record of that. Letting a
  /// continuation's transient publish failure propagate would roll back the caller's transaction and
  /// re-run the terminal path, which is worse than a chain that the watchdog can drive again.
  /// </remarks>
  [Test]
  public async Task AFailedContinuationDoesNotFailTheCompletionAsync() {
    const string PARENT_NAME = "ContinuationTestsThrows";
    SagaContinuationRegistry.Register(PARENT_NAME, new SagaContinuation(CHILD));

    var emitter = new RecordingEmitter { ThrowOnContinuation = true };
    var service = new ChainTestSagaService(PARENT_NAME, emitter);

    var won = await service.CompleteSagaAsync(
      new SagaContext(Guid.NewGuid(), Guid.NewGuid()),
      SagaStatus.Completed, "item-1", 1, 0, 1, CancellationToken.None);

    await Assert.That(won).IsTrue()
      .Because("the saga completed, and that is what the caller is being told");
  }

  private sealed class RecordingEmitter : ISagaEventEmitter {
    public List<(string ClaimKey, IEvent Event)> PublishedOnce { get; } = [];
    public bool WinClaims { get; init; } = true;
    public bool ThrowOnContinuation { get; init; }

    public Task PublishAsync<TEvent>(TEvent eventData) where TEvent : IEvent => Task.CompletedTask;

    public Task<bool> PublishOnceAsync<TEvent>(
        string claimKey, TEvent eventData, CancellationToken cancellationToken) where TEvent : IEvent {
      if (ThrowOnContinuation && eventData is SagaContinuationRequestedEvent) {
        throw new InvalidOperationException("transient publish failure");
      }

      PublishedOnce.Add((claimKey, eventData!));
      return Task.FromResult(WinClaims);
    }
  }

  private sealed class ChainInitiatedEvent : ISagaInitiatedEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public IReadOnlyList<string> ItemIdentifiers { get; set; } = [];
    public int TotalItems { get; set; }
    public IReadOnlyList<string>? HookNames { get; set; }
  }
  private sealed class ChainItemsDispatchedEvent : ISagaItemsDispatchedEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public int TotalItems { get; set; }
    public int SuccessfullyDispatched { get; set; }
    public int FailedToDispatch { get; set; }
  }
  private sealed class ChainItemStartedEvent : ISagaItemStartedEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class ChainItemCompletedEvent : ISagaItemCompletedEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class ChainItemFailedEvent : ISagaItemFailedEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public Guid SagaId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public string? DisplayName { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string? ErrorDetails { get; set; }
  }
  private sealed class ChainCompletedEvent : ISagaCompletedEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public SagaStatus FinalStatus { get; set; }
    public string? CompletedByItemIdentifier { get; set; }
    public int CompletedItems { get; set; }
    public int FailedItems { get; set; }
    public int TotalItems { get; set; }
  }
  private sealed class ChainResetEvent : ISagaResetEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public string ItemIdentifier { get; set; } = "";
    public SagaItemState PreviousStatus { get; set; }
  }
  private sealed class ChainHookStartedEvent : ISagaHookStartedEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public string HookName { get; set; } = "";
    public string? DisplayName { get; set; }
  }
  private sealed class ChainHookCompletedEvent : ISagaHookCompletedEvent {
    public string SagaName { get; set; } = "";
    public Guid EntityId { get; set; }
    public string HookName { get; set; } = "";
    public string? DisplayName { get; set; }
    public SagaItemState Status { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorDetails { get; set; }
  }

  private sealed class ChainTestSagaService(string sagaName, ISagaEventEmitter emitter)
    : BaseSagaService<ChainInitiatedEvent, ChainItemsDispatchedEvent, ChainItemStartedEvent,
                      ChainItemCompletedEvent, ChainItemFailedEvent, ChainCompletedEvent,
                      ChainResetEvent, ChainHookStartedEvent, ChainHookCompletedEvent>(
        sagaName, emitter, NullLogger<ChainTestSagaService>.Instance) {

    protected override ChainInitiatedEvent BuildInitiatedEvent(SagaContext ctx, IReadOnlyList<string> itemIdentifiers, IReadOnlyList<string>? hookNames, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, ItemIdentifiers = itemIdentifiers, TotalItems = itemIdentifiers.Count, HookNames = hookNames };
    protected override ChainItemsDispatchedEvent BuildItemsDispatchedEvent(SagaContext ctx, int totalItems, int successfullyDispatched, int failedToDispatch, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, TotalItems = totalItems, SuccessfullyDispatched = successfullyDispatched, FailedToDispatch = failedToDispatch };
    protected override ChainItemStartedEvent BuildItemStartedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };
    protected override ChainItemCompletedEvent BuildItemCompletedEvent(SagaContext ctx, string itemIdentifier, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName };
    protected override ChainItemFailedEvent BuildItemFailedEvent(SagaContext ctx, string itemIdentifier, string errorMessage, string? errorDetails, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, SagaId = ctx.SagaId, ItemIdentifier = itemIdentifier, DisplayName = displayName, ErrorMessage = errorMessage, ErrorDetails = errorDetails };
    protected override ChainCompletedEvent BuildCompletedEvent(SagaContext ctx, SagaStatus finalStatus, string? completedByItemIdentifier, int completedItems, int failedItems, int totalItems, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, FinalStatus = finalStatus, CompletedByItemIdentifier = completedByItemIdentifier, CompletedItems = completedItems, FailedItems = failedItems, TotalItems = totalItems };
    protected override ChainResetEvent BuildResetEvent(SagaContext ctx, string itemIdentifier, SagaItemState previousStatus, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, ItemIdentifier = itemIdentifier, PreviousStatus = previousStatus };
    protected override ChainHookStartedEvent BuildHookStartedEvent(SagaContext ctx, string hookName, string? displayName, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, HookName = hookName, DisplayName = displayName };
    protected override ChainHookCompletedEvent BuildHookCompletedEvent(SagaContext ctx, string hookName, SagaItemState status, string? errorMessage, string? errorDetails, DateTimeOffset sentAt) =>
      new() { EntityId = ctx.EntityId, HookName = hookName, Status = status, ErrorMessage = errorMessage, ErrorDetails = errorDetails };
  }
}
