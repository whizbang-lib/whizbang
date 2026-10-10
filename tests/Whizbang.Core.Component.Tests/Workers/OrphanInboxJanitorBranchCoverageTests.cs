// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The <see cref="OrphanInboxJanitor"/> constructed without a logger. The logger is optional by
/// contract (it falls back to a null logger), so a host that passes none still gets its startup sweep,
/// on both the purge path and the failure path.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/OrphanInboxJanitor.cs</code-under-test>
[Category("Workers")]
public class OrphanInboxJanitorBranchCoverageTests {

  private sealed record HandledMsg : IMessage;

  private sealed class RecordingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public int PurgeCallCount { get; private set; }
    public IReadOnlyList<PurgedOrphanInboxRow> PurgeResult { get; init; } = [];
    public bool ThrowOnPurge { get; init; }

    Task<IReadOnlyList<PurgedOrphanInboxRow>> IWorkCoordinator.PurgeOrphanInboxAsync(
        IReadOnlyList<string> handledTypeNames,
        CancellationToken cancellationToken) {
      PurgeCallCount++;
      if (ThrowOnPurge) {
        throw new InvalidOperationException("simulated purge failure");
      }
      return Task.FromResult(PurgeResult);
    }
  }

  private static async Task<OrphanInboxJanitor> _runWithoutLoggerAsync(RecordingCoordinator coordinator) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    await using var sp = services.BuildServiceProvider();
    var janitor = new OrphanInboxJanitor(
      services: sp,
      receptorSnapshot: new HandledReceptorTypeSnapshot([typeof(HandledMsg)]),
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      logger: null!);
    await janitor.StartAsync(CancellationToken.None);
    // The sweep is one-shot: its own completion is the signal the body ran to its end.
    await janitor.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
    await janitor.StopAsync(CancellationToken.None);
    return janitor;
  }

  [Test]
  public async Task NullLogger_PurgeFindsOrphans_SweepStillRunsAndCompletesAsync() {
    var coordinator = new RecordingCoordinator {
      PurgeResult = [
        new PurgedOrphanInboxRow(Guid.CreateVersion7(), "Some.Retired.Type, Some.Assembly", "RetiredHandler"),
        new PurgedOrphanInboxRow(Guid.CreateVersion7(), "Some.Retired.Type, Some.Assembly", "RetiredHandler"),
      ],
    };

    var janitor = await _runWithoutLoggerAsync(coordinator);

    await Assert.That(coordinator.PurgeCallCount).IsEqualTo(1)
      .Because("a missing logger is not a reason to skip the sweep, or to reject the host at construction");
    await Assert.That(janitor.ExecuteTask!.IsCompletedSuccessfully).IsTrue()
      .Because("reporting the purged groups must fall back to the null logger rather than fault the sweep");
  }

  [Test]
  public async Task NullLogger_PurgeFails_FailureIsAbsorbedAndTheSweepCompletesAsync() {
    var coordinator = new RecordingCoordinator { ThrowOnPurge = true };

    var janitor = await _runWithoutLoggerAsync(coordinator);

    await Assert.That(coordinator.PurgeCallCount).IsEqualTo(1);
    await Assert.That(janitor.ExecuteTask!.IsCompletedSuccessfully).IsTrue()
      .Because("the startup sweep's failure contract is 'service continues', with or without a logger to tell");
  }
}
