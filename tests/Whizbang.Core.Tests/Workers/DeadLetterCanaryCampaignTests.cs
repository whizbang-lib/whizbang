using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // test method underscores

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// <para>Locks the startup held-cohort campaign (P1 of plans/dlq-stack-intelligence.md).
/// The scenario it exists for: a mass dead-letter event ends with tens of thousands of rows
/// in HeldForReview; the bug gets fixed; an operator sets
/// <c>Whizbang__DeadLetterRecovery__RetryHeldOnStartup=Canary</c> and restarts. Probes go
/// first, the cohort releases only when every probe recovers, release is staggered
/// eligibility drained by the normal paced scans — and a Mixed verdict is REPORTED, never
/// auto-released, because a cohort spanning 34 message types (observed 2026-09-03) can
/// genuinely split.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/DeadLetterRecoveryWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/IDeadLetterRecoveryService.cs</code-under-test>
[Category("Shard2")]
public sealed class DeadLetterCanaryCampaignTests {

  // ==========================================================================
  // Fake campaign surface — records every call, scripts every verdict.
  // ==========================================================================
  private sealed class CampaignFake : IDeadLetterRecoveryService {
    public List<string> CallOrder { get; } = [];
    public int PurgeCalls;
    public int PurgeReturn { get; set; }
    public List<HeldCohort> Cohorts { get; set; } = [];
    public List<(string Fp, string Gen, int Size)> BeginCalls { get; } = [];
    public Func<string, int> BeginReturns { get; set; } = _ => 10;
    public ConcurrentDictionary<string, Queue<CanaryVerdict>> Verdicts { get; } = new();
    public List<(string Fp, string Gen)> EvaluateCalls { get; } = [];
    public List<(string Fp, TimeSpan Stagger)> ReleaseCalls { get; } = [];

    private readonly ConcurrentDictionary<int, TaskCompletionSource> _evalSignals = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _fetchSignals = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _releaseSignals = new();
    private int _evalCount;
    private int _fetchCount;
    private int _releaseCount;
    public Task EvaluateSignal(int ordinal) => _sig(_evalSignals, ordinal);
    public Task FetchSignal(int ordinal) => _sig(_fetchSignals, ordinal);
    public Task ReleaseSignal(int ordinal) => _sig(_releaseSignals, ordinal);
    private static Task _sig(ConcurrentDictionary<int, TaskCompletionSource> d, int o) =>
      d.GetOrAdd(o, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    private static void _fire(ConcurrentDictionary<int, TaskCompletionSource> d, int o) =>
      d.GetOrAdd(o, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    public Task<IReadOnlyList<DeadLetterEntry>> FetchDueAsync(int maxCount, CancellationToken ct = default) {
      _fire(_fetchSignals, Interlocked.Increment(ref _fetchCount));
      return Task.FromResult<IReadOnlyList<DeadLetterEntry>>([]);
    }
    public Task<bool> RecoverAsync(Guid deadLetterId, CancellationToken ct = default) => Task.FromResult(true);
    public Task MarkHoldingAsync(Guid deadLetterId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MarkPermanentlyFailedAsync(Guid deadLetterId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ScheduleNextAttemptAsync(Guid deadLetterId, DateTimeOffset nextAt, CancellationToken ct = default) => Task.CompletedTask;
    public int GenerationReplayReturn { get; set; }
    public Task<int> ResetForGenerationAsync(string currentGeneration, CancellationToken ct = default) =>
      Task.FromResult(GenerationReplayReturn);

    public List<UnstackedDeadLetter> Unstacked { get; set; } = [];
    public List<(Guid Id, string Hash)> RecordedStacks { get; } = [];
    public Task<IReadOnlyList<UnstackedDeadLetter>> FetchUnstackedAsync(int maxCount, CancellationToken ct = default) {
      var batch = Unstacked.Take(maxCount).ToList();
      Unstacked = [.. Unstacked.Skip(maxCount)];
      return Task.FromResult<IReadOnlyList<UnstackedDeadLetter>>(batch);
    }
    public Task RecordStackAsync(Guid deadLetterId, Whizbang.Core.DeadLetters.StackIdentity stack, CancellationToken ct = default) {
      lock (RecordedStacks) { RecordedStacks.Add((deadLetterId, stack.SequenceHash)); }
      return Task.CompletedTask;
    }

    public Task<int> PurgeUndeliverableHeldAsync(CancellationToken ct = default) {
      Interlocked.Increment(ref PurgeCalls);
      lock (CallOrder) { CallOrder.Add("purge"); }
      return Task.FromResult(PurgeReturn);
    }
    public Task<IReadOnlyList<HeldCohort>> ListHeldCohortsAsync(CancellationToken ct = default) {
      lock (CallOrder) { CallOrder.Add("list"); }
      return Task.FromResult<IReadOnlyList<HeldCohort>>([.. Cohorts]);
    }
    public List<int> BudgetsSeen { get; } = [];
    public Task<int> BeginCanaryProbesAsync(string fingerprint, string generation, int probeSize, int generationBudget, CancellationToken ct = default) {
      lock (CallOrder) { CallOrder.Add($"begin:{fingerprint}"); }
      lock (BeginCalls) { BeginCalls.Add((fingerprint, generation, probeSize)); }
      lock (BudgetsSeen) { BudgetsSeen.Add(generationBudget); }
      return Task.FromResult(BeginReturns(fingerprint));
    }
    public Task<CanaryVerdict> EvaluateCampaignAsync(string fingerprint, string generation, CancellationToken ct = default) {
      lock (EvaluateCalls) { EvaluateCalls.Add((fingerprint, generation)); }
      var verdict = Verdicts.TryGetValue(fingerprint, out var q) && q.Count > 0
        ? q.Dequeue()
        : new CanaryVerdict(CanaryVerdictKind.Pending, 0, 0, 1);
      _fire(_evalSignals, Interlocked.Increment(ref _evalCount));
      return Task.FromResult(verdict);
    }
    public Task<int> ReleaseHeldCohortAsync(string fingerprint, TimeSpan stagger, CancellationToken ct = default) {
      lock (ReleaseCalls) { ReleaseCalls.Add((fingerprint, stagger)); }
      _fire(_releaseSignals, Interlocked.Increment(ref _releaseCount));
      return Task.FromResult(100);
    }
  }

  [Test]
  public async Task NewGeneration_AutoCanaries_EvenWithTheFlagOffAsync() {
    // The deploy-triggered path: generation replay found rows from an older build, and
    // AutoCanaryOnNewGeneration (default true) runs the canary campaign without any
    // operator flag — bugs fixed by deploys self-heal their cohorts.
    var svc = new CampaignFake { Cohorts = [new("fp-auto", 100, 2)], GenerationReplayReturn = 5 };
    var opts = _opts(RetryHeldOnStartupMode.Off);
    opts.EnableGenerationReplay = true;
    var (worker, _, _, _) = _build(opts, svc);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FetchSignal(1).WaitAsync(_timeout);
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(svc.BeginCalls.Count).IsEqualTo(1)
      .Because("held rows are evidence about an OLD build; a new generation re-tests the "
             + "hypothesis with probes, automatically, at probe cost not storm cost");
  }

  [Test]
  public async Task NewGeneration_AutoCanaryOptOut_StaysInertAsync() {
    var svc = new CampaignFake { Cohorts = [new("fp-auto", 100, 2)], GenerationReplayReturn = 5 };
    var opts = _opts(RetryHeldOnStartupMode.Off);
    opts.EnableGenerationReplay = true;
    opts.AutoCanaryOnNewGeneration = false;
    var (worker, _, _, _) = _build(opts, svc);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FetchSignal(1).WaitAsync(_timeout);
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(svc.BeginCalls.Count).IsEqualTo(0);
  }

  [Test]
  public async Task BudgetExhaustedCohort_IsLogged_NotProbedAgainAsync() {
    // BeginCanaryProbesAsync returning -1 = the store says this cohort has failed its
    // campaigns on GenerationBudget distinct generations — permanently pending operator.
    var svc = new CampaignFake { Cohorts = [new("fp-spent", 100, 1)], BeginReturns = _ => -1 };
    var (worker, _, logs, _) = _build(_opts(RetryHeldOnStartupMode.Canary), svc);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FetchSignal(1).WaitAsync(_timeout);
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(svc.EvaluateCalls.Count).IsEqualTo(0)
      .Because("a budget-exhausted cohort has no live campaign to evaluate");
    var warned = logs.GetSnapshot().Any(r => r.Level == LogLevel.Warning
      && r.Message.Contains("fp-spent", StringComparison.Ordinal)
      && r.Message.Contains("generation budget", StringComparison.OrdinalIgnoreCase));
    await Assert.That(warned).IsTrue()
      .Because("permanent-pending-operator is an operator decision point and must be SAID");
  }

  private static DeadLetterRecoveryWorker _buildWith(
      DeadLetterRecoveryOptions options, IDeadLetterRecoveryService svc) {
    var services = new ServiceCollection();
    services.AddFakeLogging();
    services.AddSingleton(svc);
    services.AddSingleton<IDeadLetterRecoveryPolicy>(
      new DefaultDeadLetterRecoveryPolicy(Options.Create(new DeadLetterRecoveryOptions())));
    var provider = services.BuildServiceProvider();
    return new DeadLetterRecoveryWorker(
      provider.GetRequiredService<IServiceScopeFactory>(),
      new Gate(),
      Options.Create(options),
      new Gen(),
      provider.GetRequiredService<ILogger<DeadLetterRecoveryWorker>>());
  }

  [Test]
  public async Task FailedRecovery_BacksOffExponentially_PerAttemptAsync() {
    // Throttled policy: 30-minute cooldown, budget 3. At recovery_attempts=2 the backoff
    // is 30 x 2^2 = 120 minutes — exponential, not metronomic — and capped at 24 hours.
    var entry = new DeadLetterEntry(
      DeadLetterId: Guid.NewGuid(), SourceTable: "wh_inbox", SourceId: Guid.NewGuid(),
      StreamId: null, MessageType: "T.A", FailureReason: MessageFailureReason.Throttled,
      AttemptsWhenDlq: 1, DeadLetteredAt: DateTimeOffset.UtcNow,
      RecoveryStatus: DeadLetterRecoveryStatus.Pending, RecoveryAttempts: 2,
      Generation: "g/1");
    var svc = new FailingRecoveryFake(entry);
    var worker = _buildWith(_opts(RetryHeldOnStartupMode.Off), svc);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.Scheduled.Task.WaitAsync(_timeout);
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    var delay = svc.ScheduledAt!.Value - DateTimeOffset.UtcNow;
    await Assert.That(delay > TimeSpan.FromMinutes(100)).IsTrue()
      .Because("Throttled policy cooldown is 30 minutes; attempt 2 backs off 30 x 2^2 = 120 "
             + "minutes (less test runtime) — exponential, not metronomic");
    await Assert.That(delay < TimeSpan.FromHours(25)).IsTrue()
      .Because("and the backoff is capped at 24 hours so a row can never schedule itself "
             + "into next month");
  }

  private sealed class FailingRecoveryFake(DeadLetterEntry entry) : IDeadLetterRecoveryService {
    private bool _served;
    public TaskCompletionSource Scheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public DateTimeOffset? ScheduledAt { get; private set; }
    public Task<IReadOnlyList<DeadLetterEntry>> FetchDueAsync(int maxCount, CancellationToken ct = default) {
      if (_served) { return Task.FromResult<IReadOnlyList<DeadLetterEntry>>([]); }
      _served = true;
      return Task.FromResult<IReadOnlyList<DeadLetterEntry>>([entry]);
    }
    public Task<bool> RecoverAsync(Guid deadLetterId, CancellationToken ct = default) =>
      throw new InvalidOperationException("recovery fails — the point of this fake");
    public Task MarkHoldingAsync(Guid deadLetterId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MarkPermanentlyFailedAsync(Guid deadLetterId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ScheduleNextAttemptAsync(Guid deadLetterId, DateTimeOffset nextAt, CancellationToken ct = default) {
      ScheduledAt = nextAt;
      Scheduled.TrySetResult();
      return Task.CompletedTask;
    }
    public int GenerationReplayReturn { get; set; }
    public Task<int> ResetForGenerationAsync(string currentGeneration, CancellationToken ct = default) =>
      Task.FromResult(GenerationReplayReturn);
    public Task<int> PurgeUndeliverableHeldAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task<IReadOnlyList<HeldCohort>> ListHeldCohortsAsync(CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<HeldCohort>>([]);
    public Task<int> BeginCanaryProbesAsync(string fingerprint, string generation, int probeSize, int generationBudget, CancellationToken ct = default) =>
      Task.FromResult(0);
    public Task<CanaryVerdict> EvaluateCampaignAsync(string fingerprint, string generation, CancellationToken ct = default) =>
      Task.FromResult(new CanaryVerdict(CanaryVerdictKind.Pass, 0, 0, 0));
    public Task<int> ReleaseHeldCohortAsync(string fingerprint, TimeSpan stagger, CancellationToken ct = default) =>
      Task.FromResult(0);
    public Task<IReadOnlyList<UnstackedDeadLetter>> FetchUnstackedAsync(int maxCount, CancellationToken ct = default) =>
      Task.FromResult<IReadOnlyList<UnstackedDeadLetter>>([]);
    public Task RecordStackAsync(Guid deadLetterId, Whizbang.Core.DeadLetters.StackIdentity stack, CancellationToken ct = default) =>
      Task.CompletedTask;
  }

  private sealed class Bell : Whizbang.Core.Notifications.IWorkNotificationListener {
    public bool IsHealthy => true;
    public DateTimeOffset? LastSignalAt => null;
    private Action<Whizbang.Core.Notifications.WorkSignalCategory>? _onSignal;
    public event Action<Whizbang.Core.Notifications.WorkSignalCategory>? OnSignal {
      add { _onSignal += value; }
      remove { _onSignal -= value; }
    }
    public event Action<bool>? OnHealthChanged { add { } remove { } }
    public void Ring() => _onSignal?.Invoke(Whizbang.Core.Notifications.WorkSignalCategory.DeadLetterReady);
  }

  private sealed class Gate : ISchemaReadyGate {
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void MarkReady() { }
    public bool IsReady => true;
  }
  private sealed class Gen : IGenerationProvider {
    public string GetGeneration() => "build/9.9.9";
  }

  private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

  private static (DeadLetterRecoveryWorker Worker, CampaignFake Svc, FakeLogCollector Logs, Bell Bell) _build(
      DeadLetterRecoveryOptions options, CampaignFake? svc = null) {
    svc ??= new CampaignFake();
    var bell = new Bell();
    var services = new ServiceCollection();
    services.AddFakeLogging();
    services.AddSingleton<IDeadLetterRecoveryService>(svc);
    services.AddSingleton<IDeadLetterRecoveryPolicy>(
      new DefaultDeadLetterRecoveryPolicy(Options.Create(new DeadLetterRecoveryOptions())));
    var provider = services.BuildServiceProvider();
    var worker = new DeadLetterRecoveryWorker(
      provider.GetRequiredService<IServiceScopeFactory>(),
      new Gate(),
      Options.Create(options),
      new Gen(),
      provider.GetRequiredService<ILogger<DeadLetterRecoveryWorker>>(),
      notificationListener: bell);
    return (worker, svc, provider.GetFakeLogCollector(), bell);
  }

  private static DeadLetterRecoveryOptions _opts(RetryHeldOnStartupMode mode) => new() {
    ScanIntervalMinutes = 1,
    EnableGenerationReplay = false,
    WaitForIdle = false,
    RetryHeldOnStartup = mode,
    CanaryProbeSize = 7,
    ReleaseStaggerMinutes = 45,
  };

  [Test]
  public async Task Off_ByDefault_TouchesNoCampaignSurfaceAsync() {
    var (worker, svc, _, _) = _build(_opts(RetryHeldOnStartupMode.Off));
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FetchSignal(1).WaitAsync(_timeout);
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(svc.PurgeCalls).IsEqualTo(0)
      .Because("Off is the default and must be genuinely inert — held rows are an operator "
             + "decision until an operator sets the mode");
    await Assert.That(svc.BeginCalls.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Canary_Startup_PurgesFirst_ThenProbesEveryCohortAsync() {
    var svc = new CampaignFake {
      Cohorts = [new("fp-aaa", 5000, 3), new("fp-bbb", 200, 1)],
      PurgeReturn = 12,
    };
    var (worker, _, _, bell) = _build(_opts(RetryHeldOnStartupMode.Canary), svc);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FetchSignal(1).WaitAsync(_timeout);
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    List<string> order;
    lock (svc.CallOrder) { order = [.. svc.CallOrder]; }
    await Assert.That(order.IndexOf("purge")).IsLessThan(order.IndexOf("list"))
      .Because("the grandfather gate runs first: campaigns must only ever operate on rows "
             + "the machinery can actually re-drive");
    await Assert.That(svc.BeginCalls.Count).IsEqualTo(2);
    await Assert.That(svc.BeginCalls.All(b => b.Gen == "build/9.9.9" && b.Size == 7)).IsTrue()
      .Because("probes carry the build generation (campaign identity) and the configured size");
  }

  [Test]
  public async Task Canary_PassVerdict_ReleasesTheCohort_WithConfiguredStaggerAsync() {
    var svc = new CampaignFake { Cohorts = [new("fp-aaa", 5000, 3)] };
    svc.Verdicts["fp-aaa"] = new Queue<CanaryVerdict>([new(CanaryVerdictKind.Pass, 7, 0, 0)]);
    var (worker, _, _, bell) = _build(_opts(RetryHeldOnStartupMode.Canary), svc);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.ReleaseSignal(1).WaitAsync(_timeout);
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(svc.ReleaseCalls.Count).IsEqualTo(1);
    await Assert.That(svc.ReleaseCalls[0].Fp).IsEqualTo("fp-aaa");
    await Assert.That(svc.ReleaseCalls[0].Stagger).IsEqualTo(TimeSpan.FromMinutes(45))
      .Because("release is staggered eligibility — the configured window reaches the store");
  }

  [Test]
  public async Task Canary_FailVerdict_KeepsHeld_AndStopsEvaluatingAsync() {
    var svc = new CampaignFake { Cohorts = [new("fp-aaa", 5000, 3)] };
    svc.Verdicts["fp-aaa"] = new Queue<CanaryVerdict>([new(CanaryVerdictKind.Fail, 0, 7, 0)]);
    var (worker, _, _, bell) = _build(_opts(RetryHeldOnStartupMode.Canary), svc);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.EvaluateSignal(1).WaitAsync(_timeout);
    // Drive one more full scan so a lingering campaign WOULD have been evaluated again.
    bell.Ring();
    await svc.FetchSignal(2).WaitAsync(_timeout);
    bell.Ring();
    await svc.FetchSignal(3).WaitAsync(_timeout);
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(svc.ReleaseCalls.Count).IsEqualTo(0)
      .Because("every probe failed — the cohort stays exactly where it was");
    await Assert.That(svc.EvaluateCalls.Count).IsEqualTo(1)
      .Because("a failed campaign is closed, not re-polled forever");
  }

  [Test]
  public async Task Canary_MixedVerdict_ReportsTheSplit_AndNeverReleasesAsync() {
    var svc = new CampaignFake { Cohorts = [new("fp-aaa", 5000, 3)] };
    svc.Verdicts["fp-aaa"] = new Queue<CanaryVerdict>([new(CanaryVerdictKind.Mixed, 4, 3, 0)]);
    var (worker, _, logs, bell) = _build(_opts(RetryHeldOnStartupMode.Canary), svc);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.EvaluateSignal(1).WaitAsync(_timeout);
    bell.Ring();
    await svc.FetchSignal(2).WaitAsync(_timeout);
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(svc.ReleaseCalls.Count).IsEqualTo(0)
      .Because("a split cohort auto-released would re-drive the failing half at full "
             + "volume — Mixed is an operator decision by design");
    var warning = logs.GetSnapshot().FirstOrDefault(r => r.Level == LogLevel.Warning
      && r.Message.Contains("fp-aaa", StringComparison.Ordinal));
    await Assert.That(warning is not null).IsTrue()
      .Because("a Mixed verdict nobody hears about is a cohort parked silently — the split "
             + "(succeeded/failed) must be SAID with the fingerprint");
  }

  [Test]
  public async Task Canary_PendingVerdict_IsEvaluatedAgainNextScanAsync() {
    var svc = new CampaignFake { Cohorts = [new("fp-aaa", 5000, 3)] };
    svc.Verdicts["fp-aaa"] = new Queue<CanaryVerdict>([
      new(CanaryVerdictKind.Pending, 3, 0, 4),
      new(CanaryVerdictKind.Pass, 7, 0, 0),
    ]);
    var (worker, _, _, bell) = _build(_opts(RetryHeldOnStartupMode.Canary), svc);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.EvaluateSignal(1).WaitAsync(_timeout);
    bell.Ring();
    await svc.ReleaseSignal(1).WaitAsync(_timeout);
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(svc.EvaluateCalls.Count).IsEqualTo(2)
      .Because("outstanding probes mean evaluate again next scan, not verdict-by-timeout");
    await Assert.That(svc.ReleaseCalls.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Full_ReleasesEveryCohort_WithoutProbingAsync() {
    var svc = new CampaignFake { Cohorts = [new("fp-aaa", 5000, 3), new("fp-bbb", 200, 1)] };
    var (worker, _, _, bell) = _build(_opts(RetryHeldOnStartupMode.Full), svc);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.ReleaseSignal(2).WaitAsync(_timeout);
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(svc.BeginCalls.Count).IsEqualTo(0)
      .Because("Full is the operator's trust shortcut past the verdict");
    await Assert.That(svc.ReleaseCalls.Count).IsEqualTo(2);
    await Assert.That(svc.ReleaseCalls.All(r => r.Stagger == TimeSpan.FromMinutes(45))).IsTrue()
      .Because("but never a pacing shortcut: Full keeps the staggered release");
    await Assert.That(svc.PurgeCalls).IsEqualTo(1)
      .Because("the grandfather gate applies to Full too");
  }

  [Test]
  public async Task Scan_BackfillsUnstackedRows_WithTheNormalizerHashAsync() {
    var id = Guid.NewGuid();
    var text = "System.InvalidOperationException: x\n   at A.B.<M>d__1.MoveNext()";
    var svc = new CampaignFake { Unstacked = [new(id, text)] };
    var (worker, _, _, _) = _build(_opts(RetryHeldOnStartupMode.Off), svc);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FetchSignal(1).WaitAsync(_timeout);
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    var expected = Whizbang.Core.DeadLetters.StackNormalizer.Normalize(text)!.SequenceHash;
    List<(Guid, string)> recorded;
    lock (svc.RecordedStacks) { recorded = [.. svc.RecordedStacks]; }
    await Assert.That(recorded.Count).IsEqualTo(1);
    await Assert.That(recorded[0].Item2).IsEqualTo(expected)
      .Because("the backfill uses the SAME normalizer as the inline metric — one "
             + "implementation is the whole point; two would drift and split cohorts");
  }

  [Test]
  public async Task Scan_BackfillDisabled_TouchesNothingAsync() {
    var svc = new CampaignFake { Unstacked = [new(Guid.NewGuid(), "boom")] };
    var opts = _opts(RetryHeldOnStartupMode.Off);
    opts.StackBackfillBatchSize = 0;
    var (worker, _, _, _) = _build(opts, svc);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.FetchSignal(1).WaitAsync(_timeout);
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(svc.RecordedStacks.Count).IsEqualTo(0)
      .Because("0 is the off switch, and an off switch that trickles is the config-scenery "
             + "disease this arc exists to kill");
  }

  [Test]
  public async Task Canary_WithNoRecoveryService_WarnsOnce_AndDoesNotThrowAsync() {
    var services = new ServiceCollection();
    services.AddFakeLogging();
    await using var provider = services.BuildServiceProvider();
    var worker = new DeadLetterRecoveryWorker(
      provider.GetRequiredService<IServiceScopeFactory>(),
      new Gate(),
      Options.Create(_opts(RetryHeldOnStartupMode.Canary)),
      new Gen(),
      provider.GetRequiredService<ILogger<DeadLetterRecoveryWorker>>());
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    var logs = provider.GetFakeLogCollector();
    var deadline = Task.Delay(TimeSpan.FromSeconds(10));
    while (!logs.GetSnapshot().Any(r => r.Level == LogLevel.Warning
             && r.Message.Contains("IDeadLetterRecoveryService", StringComparison.Ordinal))) {
      if (deadline.IsCompleted) { break; }
      await Task.Yield();
    }
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    var warnings = logs.GetSnapshot().Count(r => r.Level == LogLevel.Warning
      && r.Message.Contains("IDeadLetterRecoveryService", StringComparison.Ordinal));
    await Assert.That(warnings).IsEqualTo(1)
      .Because("a campaign against a host with no recovery service is the same silent-death "
             + "shape the lifecycle hardening closed — said once, then quiet");
  }

  [Test]
  public async Task Canary_ExistingCampaign_IsStillEvaluatedAsync() {
    // Begin returning 0 = a campaign for this (fingerprint, generation) already exists —
    // e.g. the pod restarted mid-campaign. The worker must still evaluate it.
    var svc = new CampaignFake { Cohorts = [new("fp-aaa", 5000, 3)], BeginReturns = _ => 0 };
    svc.Verdicts["fp-aaa"] = new Queue<CanaryVerdict>([new(CanaryVerdictKind.Pass, 7, 0, 0)]);
    var (worker, _, _, bell) = _build(_opts(RetryHeldOnStartupMode.Canary), svc);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await svc.ReleaseSignal(1).WaitAsync(_timeout);
    cts.Cancel();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(svc.ReleaseCalls.Count).IsEqualTo(1)
      .Because("a restart mid-campaign resumes the campaign, it does not orphan it");
  }
}
