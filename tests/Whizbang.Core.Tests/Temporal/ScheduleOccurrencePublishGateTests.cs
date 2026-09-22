using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Temporal;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Temporal;

/// <summary>
/// Unit tests for <see cref="ScheduleOccurrencePublishGate"/> — the pre-fire gate. Deterministic (fakes
/// for the hook / occurrence store / schedule manager; no database). Covers the load-bearing invariants:
/// non-occurrence messages and hook-less hosts are untouched, each FireDecision maps to the right publish
/// outcome and side effects, and a THROWING hook fails open (runs the job) rather than silently dropping it.
/// </summary>
/// <docs>fundamentals/temporal/pre-fire-hook</docs>
public class ScheduleOccurrencePublishGateTests {
  private static readonly Guid _schedule = Guid.Parse("11111111-1111-1111-1111-111111111111");
  private static readonly Guid _authority = Guid.Parse("22222222-2222-2222-2222-222222222222");

  private sealed class FakeHook : IScheduleFireHook {
    private readonly FireDecision _decision;
    private readonly bool _throw;
    public ScheduleFireContext? Seen { get; private set; }
    public FakeHook(FireDecision decision) => _decision = decision;
    private FakeHook(bool shouldThrow) => _throw = shouldThrow;
    public static FakeHook Throwing() => new(true);

    public ValueTask<FireDecision> OnBeforeFireAsync(ScheduleFireContext context, CancellationToken cancellationToken = default) {
      Seen = context;
      if (_throw) {
        throw new InvalidOperationException("hook blew up");
      }
      return ValueTask.FromResult(_decision);
    }
  }

  private sealed class FakeStore : IScheduleOccurrenceStore {
    public (Guid Id, DateTimeOffset Until)? Deferred { get; private set; }
    public (Guid ScheduleId, short Status, string? Note)? Logged { get; private set; }
    public (Guid ScheduleId, string Claims)? Refreshed { get; private set; }

    public Task DeferAsync(Guid occurrenceId, DateTimeOffset until, CancellationToken cancellationToken = default) {
      Deferred = (occurrenceId, until);
      return Task.CompletedTask;
    }
    public Task LogRunAsync(Guid scheduleId, Guid occurrenceId, short status, string? note, CancellationToken cancellationToken = default) {
      Logged = (scheduleId, status, note);
      return Task.CompletedTask;
    }
    public Task RefreshAuthorityClaimsAsync(Guid scheduleId, string claimsJson, CancellationToken cancellationToken = default) {
      Refreshed = (scheduleId, claimsJson);
      return Task.CompletedTask;
    }
  }

  private sealed class FakeManager : IScheduleManager {
    public Guid? Canceled { get; private set; }
    public Task<bool> CancelAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) {
      Canceled = scheduleId;
      return Task.FromResult(true);
    }
    public Task<ScheduleHandle> CreateAsync(ScheduleDefinition definition, CancellationToken cancellationToken = default) =>
      Task.FromResult(new ScheduleHandle(Guid.NewGuid(), default, true));
    public Task<bool> PauseAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);
    public Task<bool> ResumeAsync(Guid scheduleId, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);
    public Task<Guid?> TriggerNowAsync(Guid scheduleId, CancellationToken cancellationToken = default) =>
      Task.FromResult<Guid?>(null);
    public Task<ScheduleUpdateResult?> UpdateAsync(Guid scheduleId, ScheduleUpdate update, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
      Task.FromResult<ScheduleUpdateResult?>(null);
  }

  private static OutboxWork _work(string? metadataJson, Guid? messageId = null) {
    var id = messageId ?? TrackedGuid.NewMedo().Value;   // MessageId enforces UUIDv7
    return new OutboxWork {
      MessageId = id,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(id),
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = [],
        Payload = JsonDocument.Parse("{}").RootElement,
      },
      EnvelopeType = "env",
      MessageType = "PaymentTimedOut",
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
      MetadataJson = metadataJson,
    };
  }

  private static string _occurrenceMetadata(string? claims = null) =>
    $$"""
      {"scheduleId":"{{_schedule}}","occurrence":7,"deliveryGuarantee":0,
       "authorityPrincipalId":"{{_authority}}","authorityClaims":{{claims ?? "null"}}}
      """;

  private static (ScheduleOccurrencePublishGate Gate, FakeStore Store, FakeManager Manager) _create(IScheduleFireHook? hook) {
    var services = new ServiceCollection();
    var store = new FakeStore();
    var manager = new FakeManager();
    if (hook is not null) {
      services.AddSingleton(hook);
    }
    services.AddSingleton<IScheduleOccurrenceStore>(store);
    services.AddSingleton<IScheduleManager>(manager);
    var provider = services.BuildServiceProvider();
    var gate = new ScheduleOccurrencePublishGate(
      provider.GetRequiredService<IServiceScopeFactory>(),
      NullLogger<ScheduleOccurrencePublishGate>.Instance);
    return (gate, store, manager);
  }

  // ---- messages the gate must not touch ----

  [Test]
  public async Task PlainMessage_ProceedsAndHookIsNotConsultedAsync() {
    var hook = new FakeHook(FireDecision.Skip());
    var (gate, _, _) = _create(hook);

    var decision = await gate.EvaluateAsync(_work("""{"id":"abc"}"""));   // no scheduleId

    await Assert.That(decision).IsEqualTo(OccurrencePublishDecision.Proceed);
    await Assert.That(hook.Seen).IsNull()
      .Because("a non-occurrence message must never reach the schedule hook");
  }

  [Test]
  public async Task NullMetadata_ProceedsAsync() {
    var (gate, _, _) = _create(new FakeHook(FireDecision.Skip()));
    await Assert.That(await gate.EvaluateAsync(_work(null))).IsEqualTo(OccurrencePublishDecision.Proceed);
  }

  [Test]
  public async Task Occurrence_NoHookRegistered_ProceedsAsync() {
    var (gate, _, _) = _create(hook: null);
    await Assert.That(await gate.EvaluateAsync(_work(_occurrenceMetadata())))
      .IsEqualTo(OccurrencePublishDecision.Proceed)
      .Because("the gate is inert until a developer registers a hook");
  }

  // ---- the hook sees the right context ----

  [Test]
  public async Task Occurrence_HookReceivesScheduleAndAuthorityAsync() {
    var hook = new FakeHook(FireDecision.Proceed());
    var (gate, _, _) = _create(hook);
    var msg = TrackedGuid.NewMedo().Value;

    _ = await gate.EvaluateAsync(_work(_occurrenceMetadata("""{"roles":["billing"]}"""), msg));

    var ctx = hook.Seen!.Value;
    await Assert.That(ctx.ScheduleId).IsEqualTo(_schedule);
    await Assert.That(ctx.OccurrenceId).IsEqualTo(msg);
    await Assert.That(ctx.OccurrenceNumber).IsEqualTo(7L);
    await Assert.That(ctx.AuthorityPrincipalId).IsEqualTo(_authority);
    await Assert.That(ctx.AuthorityClaimsJson).Contains("billing");
    await Assert.That(ctx.EventType).IsEqualTo("PaymentTimedOut");
  }

  // ---- decisions ----

  [Test]
  public async Task Skip_DropsAndLogsSkippedRunAsync() {
    var (gate, store, manager) = _create(new FakeHook(FireDecision.Skip()));

    var decision = await gate.EvaluateAsync(_work(_occurrenceMetadata()));

    await Assert.That(decision).IsEqualTo(OccurrencePublishDecision.Drop);
    await Assert.That(store.Logged!.Value.Status).IsEqualTo((short)2);   // Skipped
    await Assert.That(manager.Canceled).IsNull()
      .Because("skip drops this occurrence only — the schedule keeps its cadence");
  }

  [Test]
  public async Task Cancel_DropsAndVoidsTheScheduleAsync() {
    var (gate, store, manager) = _create(new FakeHook(FireDecision.Cancel()));

    var decision = await gate.EvaluateAsync(_work(_occurrenceMetadata()));

    await Assert.That(decision).IsEqualTo(OccurrencePublishDecision.Drop);
    await Assert.That(manager.Canceled).IsEqualTo(_schedule);
    await Assert.That(store.Logged!.Value.Status).IsEqualTo((short)2);
  }

  [Test]
  public async Task Defer_ReschedulesSameOccurrenceAsync() {
    var until = new DateTimeOffset(2026, 08, 01, 12, 00, 00, TimeSpan.Zero);
    var (gate, store, _) = _create(new FakeHook(FireDecision.Defer(until)));
    var msg = TrackedGuid.NewMedo().Value;

    var decision = await gate.EvaluateAsync(_work(_occurrenceMetadata(), msg));

    await Assert.That(decision).IsEqualTo(OccurrencePublishDecision.Deferred);
    await Assert.That(store.Deferred!.Value.Id).IsEqualTo(msg)
      .Because("defer retries the SAME occurrence — it is not dropped and not re-created");
    await Assert.That(store.Deferred!.Value.Until).IsEqualTo(until);
  }

  [Test]
  public async Task Proceed_WithRefreshedClaims_WritesSnapshotBackAsync() {
    var (gate, store, _) = _create(new FakeHook(FireDecision.Proceed("""{"roles":["reduced"]}""")));

    var decision = await gate.EvaluateAsync(_work(_occurrenceMetadata("""{"roles":["billing"]}""")));

    await Assert.That(decision).IsEqualTo(OccurrencePublishDecision.Proceed);
    await Assert.That(store.Refreshed!.Value.ScheduleId).IsEqualTo(_schedule);
    await Assert.That(store.Refreshed!.Value.Claims).Contains("reduced")
      .Because("a refreshed snapshot must be persisted so subsequent fires start from fresh claims");
  }

  // ---- the safety invariant ----

  [Test]
  public async Task ThrowingHook_FailsOpenAndStillRunsTheJobAsync() {
    var (gate, store, manager) = _create(FakeHook.Throwing());

    var decision = await gate.EvaluateAsync(_work(_occurrenceMetadata()));

    await Assert.That(decision).IsEqualTo(OccurrencePublishDecision.Proceed)
      .Because("a buggy hook must not silently swallow scheduled work");
    await Assert.That(store.Logged).IsNull();
    await Assert.That(manager.Canceled).IsNull();
  }
}
