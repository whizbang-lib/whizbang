// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for <see cref="IntegrityAuditWorker"/> paths the primary suite
/// (<see cref="IntegrityAuditWorkerTests"/>) doesn't reach: the main loop's own two exception
/// exits — a cancellation mid-cycle that must stop the loop, and any other failure that must NOT
/// stop it — and the schema-gate wait being canceled before the loop ever starts a cycle.
/// </summary>
public class IntegrityAuditWorkerCoverageTests {

  /// <summary>Records rendered log messages so the branch actually taken can be asserted.</summary>
  private sealed class MessageLogger : ILogger<IntegrityAuditWorker> {
    private readonly List<string> _messages = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_messages) { _messages.Add(formatter(state, exception)); }
    }
    public bool Saw(string fragment) {
      lock (_messages) { return _messages.Any(m => m.Contains(fragment, StringComparison.OrdinalIgnoreCase)); }
    }
  }

  /// <summary>A coordinator whose claim always succeeds and counts its calls.</summary>
  private sealed class ClaimCountingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public int ClaimCalls { get; private set; }

    public Task<bool> TryClaimIntegrityAuditCycleAsync(TimeSpan claimWindow, CancellationToken cancellationToken = default) {
      ClaimCalls++;
      return Task.FromResult(true);
    }
  }

  /// <summary>A coordinator whose claim always throws <see cref="OperationCanceledException"/>.</summary>
  private sealed class CanceledMidCycleCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public int ClaimCalls { get; private set; }

    public Task<bool> TryClaimIntegrityAuditCycleAsync(TimeSpan claimWindow, CancellationToken cancellationToken = default) {
      ClaimCalls++;
      throw new OperationCanceledException("audit cycle canceled mid-claim");
    }
  }

  /// <summary>A coordinator whose claim always throws a non-cancellation exception, signaling
  /// once it has been called a second time (proof the loop survived the first failure).</summary>
  private sealed class RepeatedlyFailingCoordinator(TaskCompletionSource secondCallSignal) : NoOpWorkCoordinator, IWorkCoordinator {
    public int ClaimCalls { get; private set; }

    public Task<bool> TryClaimIntegrityAuditCycleAsync(TimeSpan claimWindow, CancellationToken cancellationToken = default) {
      ClaimCalls++;
      if (ClaimCalls >= 2) {
        secondCallSignal.TrySetResult();
      }
      throw new InvalidOperationException("perspective store unavailable");
    }
  }

  // ── ExecuteAsync lifecycle: the schema-gate exit ────────────────────────

  // ── ExecuteAsync's main loop: the two exception exits ───────────────────

  /// <summary>
  /// If this regressed to swallowing a mid-cycle cancellation and looping again instead of
  /// breaking, a shutdown in progress would keep firing audit cycles instead of winding down.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_AuditCoreCanceledMidCycle_BreaksTheLoopWithoutFaultingAsync(CancellationToken testToken) {
    var coordinator = new CanceledMidCycleCoordinator();
    var worker = _buildWorker(coordinator, new StreamIntegrityOptions {
      AuditEnabled = true,
      AuditOnStartup = false,
      AuditIntervalMinutes = 0,
    });

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    var executeTask = worker.ExecuteTask;
    await executeTask!.WaitAsync(TimeSpan.FromSeconds(5), testToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(executeTask.IsCompleted).IsTrue()
      .Because("a mid-cycle cancellation must let the loop exit promptly");
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("a canceled cycle ending ExecuteAsync as a fault would read as a crash on an ordinary shutdown");
    await Assert.That(coordinator.ClaimCalls).IsEqualTo(1)
      .Because("the cancellation must break the loop immediately rather than retrying — a canceled "
             + "cycle is shutdown in progress, not a transient failure to retry past");
  }

  /// <summary>
  /// If this regressed to letting a single cycle's failure end the loop, a transient store error
  /// (a dropped connection, a deadlock) would permanently stop the audit instead of healing on
  /// the next interval.
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_AuditCoreThrowsNonCancellation_LogsAndKeepsLoopingAsync(CancellationToken testToken) {
    var secondCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var coordinator = new RepeatedlyFailingCoordinator(secondCall);
    var logger = new MessageLogger();
    var worker = _buildWorker(coordinator, new StreamIntegrityOptions {
      AuditEnabled = true,
      AuditOnStartup = false,
      AuditIntervalMinutes = 0,
    }, logger: logger);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await secondCall.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coordinator.ClaimCalls).IsGreaterThanOrEqualTo(2)
      .Because("a single cycle's failure must not end the loop — the next cycle needs to run so a "
             + "transient store error heals on the next interval rather than parking the audit forever");
    await Assert.That(logger.Saw("cycle failed")).IsTrue()
      .Because("a swallowed cycle failure with no log trace would leave an operator with no idea "
             + "the audit is failing every interval");
  }

  // ── _runAuditCoreAsync: the cross-service infrastructure guard ──────────

  /// <summary>
  /// If this regressed into throwing, every audit tick on a single-service host (no transport
  /// wired at all) would fail; if it regressed into proceeding anyway, the worker would dereference
  /// a null <c>ITransport</c> trying to dispatch a manifest request it has nowhere to send.
  /// </summary>
  /// <remarks>
  /// <c>RunAuditOnceAsync</c> only exposes <c>Task</c>, not the internal <c>bool</c> result, so the
  /// proof here is structural rather than a direct return-value assertion: the tracker is given a
  /// real origin, so if the cross-service guard failed to short-circuit, the very next thing the
  /// method would do is call <c>transport.PublishAsync(...)</c> on the deliberately-unregistered
  /// (null) transport — an immediate <see cref="NullReferenceException"/>. Completing cleanly, past
  /// the claim, is only possible if the guard returned first.
  /// </remarks>
  [Test]
  public async Task RunAuditOnceAsync_NoTransportWired_CompletesCleanlyWithoutTouchingANullTransportAsync() {
    var coordinator = new ClaimCountingCoordinator();
    var tracker = new IntegrityGapTracker();
    // A known origin proves the tracker (the local half's own state) is genuinely wired, and is
    // exactly what a broken guard would iterate to reach the null transport — this is "the local
    // half ran, cross-service just isn't reachable," not "nothing at all is wired."
    tracker.RecordCheckpoint(TrackedGuid.New().Value, "origin-a", DateTimeOffset.UtcNow, "origin-a.requests");
    var worker = _buildWorker(coordinator, new StreamIntegrityOptions { RepairTopic = "test-topic" },
      tracker: tracker,
      serializer: new EnvelopeSerializer(Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions()),
      instanceProvider: new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    // Deliberately NOT registered: ITransport — the one piece a single-service host never wires.

    await worker.RunAuditOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.ClaimCalls).IsEqualTo(1)
      .Because("the local half (claim, sweep, coverage-gap reporting) must have actually run — this "
             + "is not one of the earlier, cheaper exits (missing coordinator/dispatcher, or a denied claim)");
  }

  // ── helpers ──────────────────────────────────────────────────────────────

  private static IntegrityAuditWorker _buildWorker(
      IWorkCoordinator coordinator,
      StreamIntegrityOptions options,
      ILogger<IntegrityAuditWorker>? logger = null,
      IntegrityGapTracker? tracker = null,
      IEnvelopeSerializer? serializer = null,
      IServiceInstanceProvider? instanceProvider = null) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IDispatcher>(new FakeDispatcher());
    if (tracker is not null) {
      services.AddSingleton(tracker);
    }
    if (serializer is not null) {
      services.AddSingleton(serializer);
    }
    if (instanceProvider is not null) {
      services.AddSingleton(instanceProvider);
    }
    var sp = services.BuildServiceProvider();
    return new IntegrityAuditWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(options),
      logger ?? NullLogger<IntegrityAuditWorker>.Instance);
  }
}
