// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A redelivery answer marks a bucket as one repair cannot change only when it covered the bucket's deficit (#2143).
/// An answer for a recent window carries only the stream's recent events; when the receiver already holds those, the
/// answer changed nothing, but the bucket's missing events may be older than the window, so repair must keep trying.
/// The bundle carries the window it answers; one without it (an origin that predates the field) marks as before.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/RedeliveryReconciliation.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/RedeliveryAnswerWindow.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Minting/RedeliveryComposite.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityRepairLedger.cs</code-under-test>
/// <docs>resilience/stream-integrity#an-answer-marks-only-what-it-covered</docs>
[Category("Messaging")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S6966:Awaitable method should be used", Justification = "The in-process ledger's synchronous API sets up and reads the state under test.")]
public class RedeliveryAnswerCoverageTests {
  private const string EVENT_TYPE = "Contracts.OrderPlaced, Contracts";
  private static readonly DateTimeOffset _now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
  private static readonly TimeSpan _cooldown = TimeSpan.FromMinutes(5);

  private static RedeliveryComposite _bundle(Guid origin, Guid stream, RedeliveryAnswerWindow? window) => new() {
    StreamId = stream,
    OriginServiceId = origin,
    InnerEventIds = [TrackedGuid.New().Value, TrackedGuid.New().Value],
    InnerTypeNames = [EVENT_TYPE, EVENT_TYPE],
    InnerPayloads = [JsonElement.Parse("{}"), JsonElement.Parse("{}")],
    InnerCommitSequences = [301, 302],
    AnswersWindow = window is not null,
    AnsweredFromCommitSequence = window?.FromCommitSequence,
    AnsweredToCommitSequence = window?.ToCommitSequence,
  };

  private static List<InboxMessage> _children(RedeliveryComposite bundle) =>
    [.. bundle.InnerEventIds.Select((id, i) => new InboxMessage {
      MessageId = id,
      HandlerName = "OrderPlacedHandler",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = new MessageId(id),
        Payload = bundle.InnerPayloads[i],
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Outbox, Source = MessageSource.Inbox },
      },
      EnvelopeType = "MessageEnvelope",
      StreamId = bundle.StreamId,
      IsEvent = true,
      MessageType = EVENT_TYPE,
      SourceServiceId = bundle.OriginServiceId,
      SourceCommitSequence = bundle.InnerCommitSequences![i]!.Value,
    })];

  /// <summary>A store without the deferral queue: the reconcile runs inline. Every event of the bundle is present.</summary>
  private static (InMemoryIntegrityDeliveryCoordinator Coordinator, IntegrityRepairLedger Ledger, IntegrityRepairLedger.DivergenceKey Key)
      _store(Guid origin, Guid stream, bool deferring = false) {
    var coordinator = new InMemoryIntegrityDeliveryCoordinator {
      Supported = deferring,
      AdoptionAnswer = [new(stream, null, EVENT_TYPE, Present: 2, Adopted: 0)],
    };
    var ledger = new IntegrityRepairLedger();
    var key = new IntegrityRepairLedger.DivergenceKey(origin, null, EVENT_TYPE, stream);
    _ = ledger.TryBeginReport(key, 1, 2, 3, 4, _now, _cooldown);
    return (coordinator, ledger, key);
  }

  private static ServiceProvider _services(InMemoryIntegrityDeliveryCoordinator coordinator, IntegrityRepairLedger ledger,
      bool withDeliveries = false) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IIntegrityRepairLedger>(ledger);
    if (withDeliveries) {
      services.AddSingleton<IntegrityDeliveryRegistry>();
    }
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task Reconcile_AWindowedAnswerThatChangedNothing_LeavesTheBucketRepairableAsync() {
    var (origin, stream) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    var bundle = _bundle(origin, stream, new RedeliveryAnswerWindow(300, 400));
    var (coordinator, ledger, key) = _store(origin, stream);
    await using var services = _services(coordinator, ledger);

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle), services, NullLogger.Instance, _now);

    await Assert.That(result.MarkedUnhealable).IsEqualTo(0)
      .Because("the answer covered only sequences 301 to 400; the bucket's missing events may be older");
    await Assert.That(ledger.TryBeginRepair(key, _now.AddDays(1), TimeSpan.FromSeconds(1), 3)).IsTrue()
      .Because("repair keeps trying a gap the answer never looked at");
  }

  [Test]
  public async Task Reconcile_AWholeHistoryAnswerThatChangedNothing_MarksTheBucketAsync() {
    var (origin, stream) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    var bundle = _bundle(origin, stream, new RedeliveryAnswerWindow(null, null));
    var (coordinator, ledger, key) = _store(origin, stream);
    await using var services = _services(coordinator, ledger);

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle), services, NullLogger.Instance, _now);

    await Assert.That(result.MarkedUnhealable).IsEqualTo(1)
      .Because("the answer covered the stream's whole history and stored nothing: asking again re-ships the same events");
    await Assert.That(ledger.TryBeginRepair(key, _now.AddDays(1), TimeSpan.FromSeconds(1), 3)).IsFalse();
  }

  [Test]
  public async Task Reconcile_AnAnswerThatDoesNotSayItsWindow_MarksAsBeforeAsync() {
    var (origin, stream) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    var bundle = _bundle(origin, stream, window: null);
    var (coordinator, ledger, _) = _store(origin, stream);
    await using var services = _services(coordinator, ledger);

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle), services, NullLogger.Instance, _now);

    await Assert.That(result.MarkedUnhealable).IsEqualTo(1)
      .Because("an origin that predates the window behaves as it did; a mixed fleet is not stranded");
  }

  [Test]
  public async Task Reconcile_GivesTheCommitTimeRecordTheAnsweredWindowAsync() {
    var (origin, stream) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    var window = new RedeliveryAnswerWindow(300, 400);
    var bundle = _bundle(origin, stream, window);
    var (coordinator, ledger, _) = _store(origin, stream);
    await using var services = _services(coordinator, ledger);

    _ = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle), services, NullLogger.Instance, _now);

    await Assert.That(coordinator.BundleAnswerWindows).IsEquivalentTo(new RedeliveryAnswerWindow?[] { window })
      .Because("the durable store judges coverage when the bundle commits, so it needs the window");
  }

  [Test]
  public async Task TheDrain_ReadsTheWindowBack_AndAWindowedAnswerLeavesTheBucketRepairableAsync() {
    var (origin, stream) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    var bundle = _bundle(origin, stream, new RedeliveryAnswerWindow(300, null));
    var (coordinator, ledger, key) = _store(origin, stream, deferring: true);
    // The commit-time record found every event already stored; the window still decides the mark.
    coordinator.BundleAnswer = new IntegrityBundleAnswer(2, 2, 0, 0, 0, [], []);
    await using var services = _services(coordinator, ledger, withDeliveries: true);
    var deferred = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle), services, NullLogger.Instance, _now);
    await Assert.That(deferred.Deferred).IsTrue().Because("precondition: the reconcile waits for the maintainer");

    _ = services.GetRequiredService<IntegrityDeliveryRegistry>().TryGetHandler(RedeliveryReconciler.DELIVERY_TYPE, out var handler);
    await handler!(coordinator.Pending[0].Payload, services, CancellationToken.None);

    await Assert.That(coordinator.Adoptions.Count).IsEqualTo(1);
    await Assert.That(ledger.TryBeginRepair(key, _now.AddDays(1), TimeSpan.FromSeconds(1), 3)).IsTrue()
      .Because("the deferred reconcile judges coverage with the window the bundle carried");
  }

  [Test]
  public async Task TheDrain_APayloadRecordedBeforeTheWindow_MarksAsBeforeAsync() {
    var (origin, stream) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    var (coordinator, ledger, key) = _store(origin, stream, deferring: true);
    await using var services = _services(coordinator, ledger, withDeliveries: true);
    var payload = $$"""
      {"originServiceId":"{{origin}}","originServiceName":null,"eventIds":["{{TrackedGuid.New().Value}}"],"commitSequences":[7],"allPresentAtCommit":true}
      """;

    _ = services.GetRequiredService<IntegrityDeliveryRegistry>().TryGetHandler(RedeliveryReconciler.DELIVERY_TYPE, out var handler);
    await handler!(payload, services, CancellationToken.None);

    await Assert.That(ledger.TryBeginRepair(key, _now.AddDays(1), TimeSpan.FromSeconds(1), 3)).IsFalse()
      .Because("a reconcile recorded by an earlier release carries no window, and is judged as it was");
  }

  [Test]
  public async Task Ledger_AWindowedMark_MarksNothing_AndAWholeHistoryMarkMarksTheTrackedBucketAsync() {
    var inMemory = new IntegrityRepairLedger();
    var key = new IntegrityRepairLedger.DivergenceKey(TrackedGuid.New().Value, null, EVENT_TYPE, TrackedGuid.New().Value);
    _ = inMemory.TryBeginReport(key, 1, 2, 3, 4, _now, _cooldown);

    var windowed = await inMemory.MarkUnhealableBatchAsync([key], new RedeliveryAnswerWindow(10, null), [], _now);
    var whole = await inMemory.MarkUnhealableBatchAsync([key], new RedeliveryAnswerWindow(null, null), [], _now);

    await Assert.That(windowed).IsEmpty()
      .Because("this ledger keeps no compared window, so its buckets are whole-history and only a whole-history answer covers them");
    await Assert.That(whole).IsEquivalentTo([key]);
  }

  [Test]
  public async Task Ledger_TheWindowedMarkByDefault_MarksAsTheMarkWithoutAWindowAsync() {
    var inner = new IntegrityRepairLedger();
    var key = new IntegrityRepairLedger.DivergenceKey(TrackedGuid.New().Value, null, EVENT_TYPE, TrackedGuid.New().Value);
    _ = inner.TryBeginReport(key, 1, 2, 3, 4, _now, _cooldown);
    IIntegrityRepairLedger ledger = new DelegatingLedger(inner);

    var marked = await ledger.MarkUnhealableBatchAsync([key], new RedeliveryAnswerWindow(10, 20), [], _now);

    await Assert.That(marked).IsEquivalentTo([key])
      .Because("a ledger that cannot judge coverage behaves as before");
  }

  [Test]
  public async Task Window_IsWholeHistory_OnlyWithNeitherBoundAsync() {
    await Assert.That(new RedeliveryAnswerWindow(null, null).IsWholeHistory).IsTrue();
    await Assert.That(new RedeliveryAnswerWindow(5, null).IsWholeHistory).IsFalse();
    await Assert.That(new RedeliveryAnswerWindow(null, 5).IsWholeHistory).IsFalse();
  }

  [Test]
  public async Task Composite_SaysItsWindow_OnlyWhenTheOriginEchoedOneAsync() {
    IIdentityPreservingComposite echoed = new RedeliveryComposite { AnswersWindow = true, AnsweredFromCommitSequence = 4 };
    IIdentityPreservingComposite silent = new RedeliveryComposite { AnsweredFromCommitSequence = 4 };
    IIdentityPreservingComposite other = new BareComposite();

    await Assert.That(echoed.AnsweredWindow).IsEqualTo(new RedeliveryAnswerWindow(4, null));
    await Assert.That(silent.AnsweredWindow).IsNull().Because("an origin that predates the field never sets the flag");
    await Assert.That(other.AnsweredWindow).IsNull();
  }

  private sealed class BareComposite : IIdentityPreservingComposite {
    public IReadOnlyList<Guid> InnerEventIds => [];
    public IEnumerable<IMessage> InnerEvents => [];
  }

  /// <summary>A ledger that serves only the window-less members, so the window-aware mark takes its default.</summary>
  private sealed class DelegatingLedger(IntegrityRepairLedger inner) : IIntegrityRepairLedger {
    public ValueTask<bool> TryBeginReportAsync(IntegrityRepairLedger.DivergenceKey key, long originLo, long originHi,
      long localLo, long localHi, DateTimeOffset now, TimeSpan cooldown, CancellationToken cancellationToken = default) =>
      ValueTask.FromResult(true);
    public ValueTask<bool> TryBeginRepairAsync(IntegrityRepairLedger.DivergenceKey key, DateTimeOffset now,
      TimeSpan baseBackoff, int maxAttempts, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    public ValueTask MarkHealedAsync(IntegrityRepairLedger.DivergenceKey key, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
    public ValueTask<IReadOnlyList<IntegrityRepairLedger.DivergenceKey>> MarkUnhealableBatchAsync(
        IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, DateTimeOffset now, CancellationToken cancellationToken = default) =>
      inner.MarkUnhealableBatchAsync(keys, now, cancellationToken);
  }
}
