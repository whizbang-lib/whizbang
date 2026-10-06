// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Branch coverage for <see cref="BacklogAgeWorker"/>: a non-positive configured interval falls
/// back to a one-minute cadence floor (a zero floor would peek the broker in a tight loop), and the
/// hosted loop ends through its own stop check when shutdown is requested during a peek that
/// completes normally, rather than only through a canceled delay.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/BacklogAgeWorker.cs</code-under-test>
[Category("Core")]
[Category("Observability")]
public class BacklogAgeWorkerBranchCoverageTests {

  [Test]
  public async Task NonPositiveInterval_UsesAOneMinuteCadenceFloorAsync() {
    var peek = new ScriptedPeek([_sample(depth: 3)]);
    using var worker = _worker(peek, TimeSpan.Zero);

    _ = await worker.PeekOnceAsync(CancellationToken.None);
    var busyInterval = worker.NextInterval;
    peek.Samples = [_sample(depth: 0)];
    var idleIntervals = new List<TimeSpan>();
    for (var i = 0; i < 3; i++) {
      _ = await worker.PeekOnceAsync(CancellationToken.None);
      idleIntervals.Add(worker.NextInterval);
    }

    await Assert.That(busyInterval).IsEqualTo(TimeSpan.FromMinutes(1))
      .Because("a zero interval is not a cadence (and not a valid backoff floor); the floor falls back to one minute");
    await Assert.That(idleIntervals).IsEquivalentTo(new[] {
      TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4)
    }).Because("the idle stretch starts at the fallback floor and doubles up to four times it");
  }

  [Test]
  public async Task ExecuteAsync_StopRequestedDuringACompletedPeek_EndsThroughTheLoopsStopCheckAsync() {
    using var cts = new CancellationTokenSource();
    // The peek requests shutdown and then returns normally, so nothing throws: the only way out of
    // the loop is its own check of the stopping token.
    var peek = new ScriptedPeek([_sample(depth: 1)]) { OnPeek = cts.Cancel };
    using var worker = _worker(peek, TimeSpan.FromTicks(1));

    await worker.StartAsync(cts.Token);
    await peek.Peeked.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(worker.ExecuteTask.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("a requested stop ends the duty cleanly, not as a fault or a cancellation");
    await Assert.That(peek.Calls).IsEqualTo(1)
      .Because("the loop provably ran one peek and then honored the stop instead of peeking again");
  }

  private static BacklogSample _sample(long depth) =>
    new("orders", depth, TimeSpan.FromSeconds(1)) { Transport = "test" };

  private static BacklogAgeWorker _worker(IBacklogPeek peek, TimeSpan interval) =>
    new([peek], [],
      Options.Create(new BacklogAgeOptions { Interval = interval }),
      new BacklogAgeState(),
      new BacklogAgeMetrics(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>())),
      NullLogger<BacklogAgeWorker>.Instance);

  private sealed class ScriptedPeek(IReadOnlyList<BacklogSample> samples) : IBacklogPeek {
    public IReadOnlyList<BacklogSample> Samples { get; set; } = samples;
    public Action? OnPeek { get; init; }
    public int Calls { get; private set; }
    public TaskCompletionSource Peeked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string TransportName => "test";

    public Task<IReadOnlyList<BacklogSample>> PeekAsync(CancellationToken cancellationToken) {
      Calls++;
      OnPeek?.Invoke();
      Peeked.TrySetResult();
      return Task.FromResult(Samples);
    }
  }
}
