using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Round-23 coverage for <see cref="DeadLetterRecoveryWorker"/>.
///
/// <para>
/// Two of the six requested target lines were investigated and are reported here rather than
/// driven by a flaky or impractical test:
/// </para>
/// <para>
/// <b>Line 227</b> (`break;` in the `catch (OperationCanceledException)` wrapping
/// <c>await Task.WhenAny(pollDelay, wakeTask).ConfigureAwait(false)</c>) is unreachable as
/// written: <c>Task.WhenAny</c>'s returned task completes successfully as soon as EITHER
/// constituent task reaches ANY terminal state (including Canceled or Faulted) — it never
/// propagates a constituent's exception or cancellation to its own awaiter. Since neither
/// <c>pollDelay</c> nor <c>wakeTask</c> is ever awaited a second time after the race, there is no
/// code path by which awaiting <c>Task.WhenAny(...)</c> itself throws
/// <see cref="OperationCanceledException"/>, regardless of whether <c>stoppingToken</c> is
/// pre-canceled or canceled mid-wait. This is a defensive catch around a statement that cannot
/// throw the exception type it catches.
/// </para>
/// <para>
/// <b>Lines 244-247</b> (the loop breaker closing after its cooldown elapses, inside
/// <c>_isBreakerOpen</c>) use <c>DateTimeOffset.UtcNow</c> directly with no injected
/// <c>TimeProvider</c> seam, and <c>LoopBreakerCooldownMinutes</c> is an <c>int</c> whose only
/// non-"stay open forever" values are whole minutes (the `&lt;= 0` branch means "never auto-close").
/// Reaching the close branch deterministically would require a real ≥60-second wall-clock wait
/// between two scans, which trades the hard requirement of fast, deterministic tests for one
/// branch of coverage — and this file cannot add a clock seam to the worker itself (only new test
/// files are in scope for this pass). Reported rather than driven by a slow test.
/// </para>
/// </summary>
public class DeadLetterRecoveryWorkerCoverageTests {
  private sealed class _fixedGenerationProvider(string value) : IGenerationProvider {
    public string GetGeneration() => value;
  }

  // Target: src/Whizbang.Core/Workers/DeadLetterRecoveryWorker.cs:154 — `return;` in the
  // `catch (OperationCanceledException)` around `_schemaReadyGate.WaitForReadyAsync`. Without
  // this, a pod stopped while still waiting for the schema (before the DLQ tables exist) would
  // fault its BackgroundService instead of exiting quietly, turning a routine fast restart during
  // a rolling deploy into a logged crash.
  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_CanceledWhileWaitingForSchemaReady_ReturnsQuietlyAsync(
      CancellationToken testToken) {
    var services = new ServiceCollection();
    var sp = services.BuildServiceProvider();
    var worker = new DeadLetterRecoveryWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new SchemaReadyGate(),  // never marked ready
      Options.Create(new DeadLetterRecoveryOptions { Enabled = true, ScanIntervalMinutes = 1 }),
      Options.Create(new Whizbang.Core.Messaging.StreamIntegrityOptions()),
      new _fixedGenerationProvider("test/0.0.1"),
      NullLogger<DeadLetterRecoveryWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    var executeTask = worker.ExecuteTask;
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(executeTask!.IsCompleted).IsTrue();
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("stopping while still waiting for the schema is routine, not exceptional -- there "
             + "are no DLQ tables to scan yet, so this must read as a clean exit");
    await Assert.That(worker.TotalScans).IsEqualTo(0)
      .Because("nothing may be scanned before the schema is ready");
  }
}
