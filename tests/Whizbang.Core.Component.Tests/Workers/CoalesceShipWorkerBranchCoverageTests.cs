// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.Tags;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable IDE0060, RCS1163 // Unused parameters: the fake coordinator implements interface members the tests never exercise

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Branches of <see cref="CoalesceShipWorker"/> the primary and coverage suites leave untaken: the
/// default partition count when no work-coordinator options are registered, a single with no stream
/// in the default composite, a Manual priority fold with no callback, startup recovery with no
/// resolver, and the tick-interval default when no enabled binding narrows it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/CoalesceShipWorker.cs</code-under-test>
[Category("Workers")]
public class CoalesceShipWorkerBranchCoverageTests {
  private static readonly DateTimeOffset _testNow = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
  private const string GROUP = "record-digest";

  // ── partition count ────────────────────────────────────────────────────

  [Test]
  public async Task RunOnceAsync_NoWorkCoordinatorOptionsRegistered_FoldsWithTheDefaultPartitionCountAsync() {
    var coordinator = _dueGroupCoordinator();
    var worker = _buildWorker(coordinator, _oneGroupResolver(slideSeconds: 15), options: null);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.PartitionCounts).Count().IsEqualTo(1);
    await Assert.That(coordinator.PartitionCounts[0]).IsEqualTo(10_000)
      .Because("with no WorkCoordinatorOptions registered the composite is inserted with the framework default");
  }

  [Test]
  public async Task RunOnceAsync_WorkCoordinatorOptionsRegistered_FoldsWithTheConfiguredPartitionCountAsync() {
    var coordinator = _dueGroupCoordinator();
    var worker = _buildWorker(coordinator, _oneGroupResolver(slideSeconds: 15), options: new WorkCoordinatorOptions { PartitionCount = 64 });

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.PartitionCounts).Count().IsEqualTo(1);
    await Assert.That(coordinator.PartitionCounts[0]).IsEqualTo(64);
  }

  // ── default composite: stream identity ─────────────────────────────────

  [Test]
  public async Task BuildDefaultComposite_SingleWithoutStream_CarriesEmptyGuidInItsSlotAsync() {
    var streamless = _single() with { StreamId = null };
    var streamed = _single();

    var composite = (CoalescedEventsComposite)CoalesceShipWorker.BuildDefaultComposite(new CoalesceFoldBatch {
      Group = GROUP,
      Singles = [streamless, streamed],
      Atomicity = FanoutAtomicity.Independent,
    });

    await Assert.That(composite.InnerStreamIds).Count().IsEqualTo(2);
    await Assert.That(composite.InnerStreamIds[0]).IsEqualTo(Guid.Empty)
      .Because("a single with no stream keeps its slot, so InnerStreamIds stays aligned with InnerEventIds");
    await Assert.That(composite.InnerStreamIds[1]).IsEqualTo(streamed.StreamId!.Value);
  }

  // ── priority fold ──────────────────────────────────────────────────────

  [Test]
  public async Task FoldPriority_ManualWithoutCallback_LeavesTheCompositeUndeclaredAsync() {
    var binding = new CoalescePolicyOptions { PriorityFold = CompositePriorityFold.Manual };
    var batch = new CoalesceFoldBatch {
      Group = GROUP,
      Singles = [_single(WorkPriority.INTERACTIVE), _single(WorkPriority.BACKGROUND)],
      Atomicity = FanoutAtomicity.Independent,
    };

    var priority = CoalesceShipWorker.FoldPriority(binding, batch);

    await Assert.That(priority).IsEqualTo(WorkPriority.UNDECLARED)
      .Because("Manual without a callback must not fall back to a member's number; the consumer's rules classify it");
  }

  [Test]
  public async Task FoldPriority_ManualWithCallback_UsesTheCallbacksNumberAsync() {
    var binding = new CoalescePolicyOptions { PriorityFold = CompositePriorityFold.Manual, PriorityFor = b => b.Singles.Count + 7 };
    var batch = new CoalesceFoldBatch {
      Group = GROUP,
      Singles = [_single(WorkPriority.INTERACTIVE)],
      Atomicity = FanoutAtomicity.Independent,
    };

    await Assert.That(CoalesceShipWorker.FoldPriority(binding, batch)).IsEqualTo(8);
  }

  // ── startup recovery without a resolver ────────────────────────────────

  [Test]
  public async Task RunStartupRecoveryAsync_NoResolver_ReleasesNothingAsync() {
    var coordinator = new RecordingCoordinator();
    var worker = _buildWorker(coordinator, resolver: null, options: new WorkCoordinatorOptions());

    await worker.RunStartupRecoveryAsync(CancellationToken.None);

    await Assert.That(coordinator.ReleasedGroups).IsEmpty()
      .Because("with no resolver there are no enabled groups to release");
  }

  // ── tick interval default ──────────────────────────────────────────────

  /// <summary>
  /// The tick is min(SlideSeconds)/3. When no binding narrows the minimum below its starting
  /// sentinel, the worker must fall back to the 5 second default instead of a tick derived from
  /// the sentinel itself (decades).
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_NoBindingNarrowsTheSlide_TicksOnTheFiveSecondDefaultAsync(CancellationToken testToken) {
    var time = new ArmedTimerClock(_testNow);
    var coordinator = new RecordingCoordinator();
    var worker = _buildWorker(coordinator, _oneGroupResolver(slideSeconds: int.MaxValue), new WorkCoordinatorOptions(), time);
    using var cts = new CancellationTokenSource();

    await worker.StartAsync(cts.Token);
    var tick = await time.FirstArmed.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(tick).IsEqualTo(TimeSpan.FromSeconds(5));
    await Assert.That(coordinator.StatsCalls).IsGreaterThanOrEqualTo(1)
      .Because("the timer armed is the pause after the first tick, which ran immediately");
  }

  // ── helpers ────────────────────────────────────────────────────────────

  /// <summary>A fake clock that reports the due time of the first timer the code under test arms.</summary>
  private sealed class ArmedTimerClock(DateTimeOffset start) : FakeTimeProvider(start) {
    public TaskCompletionSource<TimeSpan> FirstArmed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      var timer = base.CreateTimer(callback, state, dueTime, period);
      FirstArmed.TrySetResult(dueTime);
      return timer;
    }
  }

  private sealed class RecordingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    private readonly Lock _lock = new();
    private int _statsCalls;
    public IReadOnlyList<CoalesceGroupStats> Stats { get; init; } = [];
    public List<OutboxMessage> Pending { get; init; } = [];
    public List<string> ReleasedGroups { get; } = [];
    public List<int> PartitionCounts { get; } = [];
    public int StatsCalls => Volatile.Read(ref _statsCalls);

    public Task<IReadOnlyList<CoalesceGroupStats>> GetPendingCoalesceGroupStatsAsync(CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _statsCalls);
      return Task.FromResult(Stats);
    }

    public Task<IReadOnlyList<OutboxMessage>> FetchPendingCoalesceAsync(string group, int limit, CancellationToken cancellationToken = default) {
      lock (_lock) {
        var take = Pending.Take(limit).ToList();
        Pending.RemoveRange(0, take.Count);
        return Task.FromResult<IReadOnlyList<OutboxMessage>>(take);
      }
    }

    public Task CompleteCoalesceFoldAsync(IReadOnlyList<Guid> foldedIds, OutboxMessage[] compositeMessages, int partitionCount, CancellationToken cancellationToken = default) {
      lock (_lock) { PartitionCounts.Add(partitionCount); }
      return Task.CompletedTask;
    }

    public Task<int> ReleaseMaturedCoalesceAsync(string group, CancellationToken cancellationToken = default) {
      lock (_lock) { ReleasedGroups.Add(group); }
      return Task.FromResult(0);
    }
  }

  private static RecordingCoordinator _dueGroupCoordinator() => new() {
    // Quiet for 20 s against a 15 s slide: due.
    Stats = [new CoalesceGroupStats {
      Group = GROUP,
      PendingCount = 2,
      OldestCreatedAt = _testNow.AddSeconds(-40),
      NewestCreatedAt = _testNow.AddSeconds(-20),
    }],
    Pending = [_single(), _single()],
  };

  private static CoalesceGroupResolver _oneGroupResolver(int slideSeconds) {
    var tagOptions = new TagOptions();
    tagOptions.Coalesce(GROUP, c => c.SlideSeconds = slideSeconds);
    return new CoalesceGroupResolver(tagOptions, new FakeTimeProvider(_testNow), () => []);
  }

  private static CoalesceShipWorker _buildWorker(
      IWorkCoordinator coordinator,
      CoalesceGroupResolver? resolver,
      WorkCoordinatorOptions? options,
      FakeTimeProvider? time = null) {
    var services = new ServiceCollection();
    services.AddSingleton(coordinator);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions()));
    if (options is not null) {
      services.AddSingleton(options);
    }
    var sp = services.BuildServiceProvider();

    return new CoalesceShipWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      instanceProvider: new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      logger: NullLogger<CoalesceShipWorker>.Instance,
      compositeFactory: new CompositeFactory(),
      coalesceResolver: resolver,
      timeProvider: time ?? new FakeTimeProvider(_testNow));
  }

  private static OutboxMessage _single(int priority = 0) {
    var envelope = new MessageEnvelope<JsonElement> {
      Priority = priority,
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { record = "data" }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox }
    };
    return new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "test-topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
      EnvelopeType = "TestEnvelopeType",
      StreamId = (Guid)TrackedGuid.New(),
      IsEvent = false,
      MessageType = "TestNamespace.TestFoldedEvent, TestAssembly",
      CoalesceGroup = GROUP,
      ScheduledFor = _testNow.AddSeconds(60),
      Priority = priority,
    };
  }
}
