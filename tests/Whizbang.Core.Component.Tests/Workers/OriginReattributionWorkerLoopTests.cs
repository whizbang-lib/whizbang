// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The re-attribution worker's loop (#2008): it runs a pass every <c>ReattributionIntervalSeconds</c>, keeps running
/// after a pass that throws, and parks when disabled instead of completing, so a disabled worker is never mistaken for
/// a crashed one. Every wait is on a signal the coordinator raises, never on a duration.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/OriginReattributionWorker.cs</code-under-test>
public class OriginReattributionWorkerLoopTests {
  private sealed class CountingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    private int _reads;
    public bool ThrowOnFirstRead { get; init; }
    public TaskCompletionSource SecondRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Reads => Volatile.Read(ref _reads);

    public Task<OriginReattributionWindow?> GetOriginReattributionWindowAsync(
        string ownershipFingerprint, int maxEvents, CancellationToken cancellationToken = default) {
      var n = Interlocked.Increment(ref _reads);
      if (n >= 2) {
        SecondRead.TrySetResult();
      }
      if (n == 1 && ThrowOnFirstRead) {
        throw new InvalidOperationException("transient store failure");
      }
      return Task.FromResult<OriginReattributionWindow?>(new OriginReattributionWindow {
        AfterCommitSequence = 0, ThroughCommitSequence = 0, EventTypes = [],
      });
    }
  }

  private sealed class FirstLogSignal : ILogger<OriginReattributionWorker> {
    private readonly TaskCompletionSource _logged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Logged => _logged.Task;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => _logged.TrySetResult();
  }

  private static OriginReattributionWorker _worker(
      CountingCoordinator coordinator, StreamIntegrityOptions options, ILogger<OriginReattributionWorker>? logger = null) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton(Options.Create(new RoutingOptions().OwnDomains("App.Contracts.Orders")));
    var sp = services.BuildServiceProvider();
    return new OriginReattributionWorker(
      sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(options),
      logger ?? NullLogger<OriginReattributionWorker>.Instance);
  }

  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_WhenAPassThrows_KeepsRunningPassesAsync(CancellationToken testToken) {
    var coordinator = new CountingCoordinator { ThrowOnFirstRead = true };
    var worker = _worker(coordinator, new StreamIntegrityOptions { ReattributionIntervalSeconds = 1 });

    await worker.StartAsync(testToken);
    try {
      await coordinator.SecondRead.Task.WaitAsync(TimeSpan.FromSeconds(20), testToken);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }

    await Assert.That(coordinator.Reads).IsGreaterThanOrEqualTo(2)
      .Because("a transient store failure in one pass must not stop re-attribution for the life of the process");
    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse();
  }

  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_WhenDisabled_ParksWithoutWalkingAsync(CancellationToken testToken) {
    var coordinator = new CountingCoordinator();
    var logSignal = new FirstLogSignal();
    var worker = _worker(coordinator,
      new StreamIntegrityOptions { ReattributionEnabled = false, ReattributionIntervalSeconds = 1 }, logSignal);

    await worker.StartAsync(testToken);
    await logSignal.Logged.WaitAsync(testToken);

    await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse()
      .Because("a disabled worker parks; one that completes on its own reads as a crash to the host");
    await worker.StopAsync(CancellationToken.None);
    await Assert.That(worker.ExecuteTask!.IsFaulted).IsFalse();
    await Assert.That(coordinator.Reads).IsEqualTo(0);
  }
}
